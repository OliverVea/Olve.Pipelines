// Fixture: the shape of Olve.Pipelines' own self-deploy (build → test → beta → prod → publish CLI).
// Handlers are offline fakes so the SDK tests can build and run every step locally.
import { pipeline, step, steps, secret, optional, z, Source, FailureContext, githubSignature } from "@olivervea/pipelines";
import * as M from "./models";
import testSh from "./scripts/test.sh" with { type: "file" };

// ── secrets ───────────────────────────────────────────────────────────────────────────────────
export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token (repo tarball, poll, CLI release).");
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for image import + helm on the host.");
export const RELEASE_HOOK_SECRET = secret("RELEASE_HOOK_SECRET", "HMAC key of the GitHub release webhook.");

// ── steps (contracts) ─────────────────────────────────────────────────────────────────────────
export const checkout = step({
  name: "checkout",
  description: "Fetches the repository at the pushed commit.",
  image: "alpine/git:latest",
  input: { repo: z.string(), sha: z.string() },     // plain schemas → a mapper is required
  output: Source,
  secrets: [GITHUB_TOKEN],
});

export const build = step({
  name: "build",
  description: "Builds the image and the chart.",
  image: "gcr.io/kaniko-project/executor:debug",
  input: { source: Source },
  output: M.BuildArtifact,
  timeoutSeconds: 1800,
});

export const test = steps.script({
  name: "test",
  description: "Runs the unit tests.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  script: testSh,
  input: { source: Source },
  output: M.TestReport,
});

export const deployBeta = step({
  name: "deploy-beta",
  description: "Deploys to beta.",
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact },
  output: M.BetaDeployment,
  secrets: [SSH_PRIVATE_KEY],
  timeoutSeconds: 900,
});

export const deployProd = step({
  name: "deploy-prod",
  description: "Deploys to prod.",
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact },
  output: M.ProdDeployment,
  secrets: [SSH_PRIVATE_KEY],
});

export const publishCli = step({
  name: "publish-cli",
  description: "Publishes the pl CLI.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { artifact: M.BuildArtifact, prod: M.ProdDeployment },
  output: M.CliRelease,
  secrets: [GITHUB_TOKEN],
});

export const summary = step({
  name: "summary",
  description: "Reports what was deployed.",
  image: "alpine:latest",
  input: { beta: M.BetaDeployment, prod: optional(M.ProdDeployment), cli: optional(M.CliRelease) },
});

export const aoeTriage = step({
  name: "aoe-triage",
  description: "Opens an AI session to triage a failed deploy.",
  image: "alpine:latest",
  input: { failure: FailureContext },
});

// ── pipeline ──────────────────────────────────────────────────────────────────────────────────
export default pipeline({
  name: "Olve.Pipelines",
  description: "Build, deploy beta then prod, publish the pl CLI.",
  input: M.DeployRequest,
})
  .onPush(e => ({
    repo: e.repository.full_name,
    sha: e.after,
    deployTo: e.ref === "refs/heads/main" ? "prod" : "beta",
  }))
  .onPoll(
    {
      url: "https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/main",
      secrets: [GITHUB_TOKEN],
      headers: { Authorization: "$SECRET:GITHUB_TOKEN" },
      valuePath: "sha",
      intervalSeconds: 300,
    },
    value => ({ sha: value }),                             // z.input view: repo/deployTo take their defaults
  )
  .onWebhook(
    { name: "release", verify: githubSignature(RELEASE_HOOK_SECRET) },
    ({ headers, body }) =>                                 // null ignores the delivery
      headers["x-github-event"] === "release" ? { sha: (body as { release: { target_commitish: string } }).release.target_commitish } : null,
  )
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)                                      // both matched by model (Source)
  .stage(deployBeta)                                       // BuildArtifact from build
  .when({ deployTo: "prod" }, p => p
    .stage(deployProd)
    .when({ publishCli: true }, p => p
      .stage(publishCli)))                                 // ProdDeployment is definite inside the block
  .stage(summary)                                          // prod/cli maybe-available → optional()
  .onFailure(aoeTriage, { steps: [deployProd, deployBeta] });
