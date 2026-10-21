import { pipeline, step, steps, secret, optional, Source, FailureContext } from "@olve/pipelines";
import * as M from "./models";

// ── secrets ───────────────────────────────────────────────────────────────────────────────────
export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token (repo tarball, poll, CLI release).");
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for image import + helm on the host.");

// ── steps (contracts) ─────────────────────────────────────────────────────────────────────────
export const checkout = steps.gitCheckout({ name: "checkout", depth: 1, token: GITHUB_TOKEN });

// A custom step rather than steps.imageBuild: deploy needs the chart + version too (BuildArtifact).
export const build = step({
  name: "build",
  image: "gcr.io/kaniko-project/executor:debug",
  input: { source: Source },
  output: M.BuildArtifact,
  secrets: [GITHUB_TOKEN],
  timeoutSeconds: 1800,
});

export const test = steps.script({
  name: "test",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  script: "scripts/test.sh",
  input: { source: Source },
  output: M.TestReport,
});

export const deployBeta = step({
  name: "deploy-beta",
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact },
  output: M.BetaDeployment,
  secrets: [SSH_PRIVATE_KEY],
  timeoutSeconds: 900,
});

export const approveProd = steps.wait({
  name: "approve-prod",
  input: { beta: M.BetaDeployment },
  output: M.ProdApproval,
  resolveBy: {
    manual: true,
    check: {
      every: "5m",
      async run({ beta }, ctx) {
        const s = await ctx.http.getJson<{ healthy: boolean; soakMinutes: number }>(`${beta.url}/api/health`);
        return s.healthy && s.soakMinutes >= 30 ? { approved: true, by: "soak-check" } : ctx.notYet();
      },
    },
  },
  timeoutHours: 72,
});

export const deploy = step({
  name: "deploy",
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact, approval: M.ProdApproval },
  output: M.ProdDeployment,
  secrets: [SSH_PRIVATE_KEY],
});

export const publishCli = step({
  name: "publish-cli",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { artifact: M.BuildArtifact, prod: M.ProdDeployment },
  output: M.CliRelease,
  secrets: [GITHUB_TOKEN],
});

export const summary = step({
  name: "summary",
  image: "alpine:latest",
  input: { beta: M.BetaDeployment, prod: optional(M.ProdDeployment), cli: optional(M.CliRelease) },
});

export const aoeTriage = step({
  name: "aoe-triage",
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
    value => ({ sha: value }),
  )
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(build, test)                                      // both default-matched on Source
  .stage(deployBeta)                                       // BuildArtifact from build
  .when({ deployTo: "prod" }, p => p
    .stage(approveProd)
    .stage(deploy)
    .when({ publishCli: true }, p => p
      .stage(publishCli)))                                 // ProdDeployment is definite inside the block
  .stage(summary)                                          // prod/cli maybe-available → optional()
  .onFailure(aoeTriage, { steps: [deploy, deployBeta] });
