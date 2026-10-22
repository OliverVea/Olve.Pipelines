// In-repo CD pipeline for Olve.Pipelines v2 — v2 branch, beta only (docs/design/pipelines-v2.md §13).
//
// source → build ∥ test → deploy-beta → publish-cli: build the branch's image with kaniko (layer
// cache in the in-cluster registry) while the unit tests run; deploy the Helm release
// olve-pipelines-v2 into apps-beta over SSH once both succeeded (the stage barrier gates the deploy
// on the tests), then health-check the in-cluster Service; then build the `pl` CLI of the same
// commit, which the freshly deployed instance serves at /download.
//
// Bound on the v2 instance itself since plan milestone M4 (pipeline f26d19cf…, binding path .pipelines).
// The v1-format config.yaml beside this file is the recovery path: the v1 pipeline that runs it is
// paused, not deleted (docs/design/pipelines-v2-plan.md, E2 notes, for the rollback).
import { pipeline, secret, step, steps, Source } from "@olve/pipelines";
import * as M from "./models";

export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token used to fetch the repo tarball.");
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for importing the image and running helm on the homelab host.");

export const source = steps.gitHubSource({ name: "source", token: GITHUB_TOKEN });

export const build = steps.imageBuild({
  name: "build",
  repository: "olve-pipelines-v2",
  cacheRepo: "registry.ovea.pro/olve-pipelines-v2/cache",
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
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .when({ deployTo: "beta" }, p => p
    .stage(deployBeta)
    .stage(publishCli));
