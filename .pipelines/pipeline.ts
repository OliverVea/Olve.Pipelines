// v2's own CD pipeline (beta). config.yaml beside it is the paused v1 deployer's rollback path.
import { pipeline, secret, step, steps, Source } from "@olve/pipelines";
import * as M from "./models";

export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token used to fetch the repo tarball.");
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for importing the image and running helm on the homelab host.");

export const source = steps.gitHubSource({ name: "source", token: GITHUB_TOKEN });

export const MIRROR = "registry.ovea.pro/olve-pipelines-v2/mirror";

// kaniko under gVisor stalls pulling from mcr.microsoft.com; build pulls the Dockerfile's FROMs from this mirror.
export const mirrorBaseImages = step({
  name: "mirror-base-images",
  image: "gcr.io/go-containerregistry/crane:debug",
  timeoutSeconds: 900,
});

export const build = steps.imageBuild({
  name: "build",
  repository: "olve-pipelines-v2",
  cacheRepo: "registry.ovea.pro/olve-pipelines-v2/cache",
  registryMap: { "mcr.microsoft.com": `${MIRROR}/mcr.microsoft.com`, "index.docker.io": `${MIRROR}/docker.io` },
  timeoutSeconds: 900,
});

export const test = step({
  name: "test",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  timeoutSeconds: 1800,
});

export const deployBeta = steps.helmDeployOverSsh({
  name: "deploy-beta",
  host: "oliver@bulwark-m2",
  sshKey: SSH_PRIVATE_KEY,
  release: "olve-pipelines-v2",
  namespace: "apps-beta",
  chart: "helm",
  values: ["values-beta.yaml", "values-v2-beta.yaml"],
  set: { "slo.enabled": "false" },
  healthUrl: "http://olve-pipelines-v2.apps-beta.svc.cluster.local/api/health",
  timeoutSeconds: 900,
});

export const publishCli = step({
  name: "publish-cli",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  output: M.CliRelease,
  timeoutSeconds: 1800,
});

export default pipeline({
  name: "Olve.Pipelines v2",
  description: "Build the v2 branch with kaniko, run the unit tests, deploy it to apps-beta (olve-pipelines-v2) with Helm, and publish the pl CLI it serves.",
  input: M.DeployRequest,
})
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: "beta" }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })], mirrorBaseImages)
  .stage(build, test)
  .when({ deployTo: "beta" }, p => p
    .stage(deployBeta)
    .stage(publishCli));
