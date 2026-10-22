// v2's own CD pipeline: every push goes beta, then prod. config.yaml beside it is the paused v1 deployer's rollback path.
import { pipeline, secret, step, steps, HelmRelease, Source } from "@olve/pipelines";
import * as M from "./models";

export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token used to fetch the repo tarball.");
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for importing the image and running helm on the homelab host.");
export const NPM_TOKEN = secret("NPM_TOKEN", "npm token that publishes @olivervea/pipelines.");

export const source = steps.gitHubSource({
  name: "source",
  description: "Fetches the pushed commit from GitHub.",
  token: GITHUB_TOKEN,
});

export const build = steps.imageBuild({
  name: "build",
  description: "Builds the Olve.Pipelines image with kaniko.",
  repository: "olve-pipelines-v2",
  cacheRepo: "registry.ovea.pro/olve-pipelines-v2/cache",
  timeoutSeconds: 900,
});

export const test = step({
  name: "test",
  description: "Runs the .NET unit tests.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  timeoutSeconds: 1800,
});

export const BETA_URL = "http://olve-pipelines-v2.apps-beta.svc.cluster.local";

export const deployBeta = steps.helmDeployOverSsh({
  name: "deploy-beta",
  description: "Deploys the image to apps-beta (olve-pipelines-v2) with Helm and checks its health.",
  host: "oliver@bulwark-m2",
  sshKey: SSH_PRIVATE_KEY,
  release: "olve-pipelines-v2",
  namespace: "apps-beta",
  chart: "helm",
  values: ["values-beta.yaml", "values-v2-beta.yaml"],
  set: { "slo.enabled": "false" },
  healthUrl: `${BETA_URL}/api/health`,
  timeoutSeconds: 900,
});

export const betaTest = step({
  name: "beta-test",
  description: "Smoke-tests the freshly deployed beta: readiness, the API, the frontend bundle, the error model and SSE.",
  image: "alpine:3.22",
  input: { release: HelmRelease },
  timeoutSeconds: 180,
});

export const deployProd = steps.helmDeployOverSsh({
  name: "deploy-prod",
  description: "Deploys the image to apps (olve-pipelines-v2) with Helm and checks its health.",
  host: "oliver@bulwark-m2",
  sshKey: SSH_PRIVATE_KEY,
  release: "olve-pipelines-v2",
  namespace: "apps",
  chart: "helm",
  values: ["values-v2-prod.yaml"],
  healthUrl: "http://olve-pipelines-v2.apps.svc.cluster.local/api/health",
  timeoutSeconds: 900,
});

export const publishCli = step({
  name: "publish-cli",
  description: "Builds the pl CLI that this instance serves at /download and /install.sh.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  output: M.CliRelease,
  timeoutSeconds: 1800,
});

export const publishSdk = step({
  name: "publish-sdk",
  description: "Checks sdk/ and publishes it to npm as @olivervea/pipelines when its version is not on npm yet.",
  image: "oven/bun:1.4.3-slim",
  input: { source: Source },
  output: M.SdkRelease,
  secrets: [NPM_TOKEN],
  timeoutSeconds: 1200,
});

export default pipeline({
  name: "Olve.Pipelines v2",
  description: "Build the v2 branch with kaniko, run the unit tests, deploy it to apps-beta (olve-pipelines-v2) with Helm and smoke-test it, then deploy the same image to apps and publish the pl CLI and the SDK to npm. deployTo=beta stops after beta.",
  input: M.DeployRequest,
})
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: "prod" }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .stage(betaTest)
  .when({ deployTo: "prod" }, p => p
    .stage(deployProd)
    .stage(publishCli, publishSdk));
