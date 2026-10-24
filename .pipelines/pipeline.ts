// Olve.Pipelines deploying itself: build and verify every component once, publish prereleases,
// deploy beta, smoke-test it, deploy prod, then promote the same artifacts. Where it all goes is the
// instance's config (config.ts); config.yaml beside it is the paused v1 deployer's rollback path.
import { HelmRelease, optional, pipeline, PushedImage, Source, step, steps, type ConfigRef } from "@olivervea/pipelines";
import { AUTH, DEPLOYER_TOKEN, GHCR_TOKEN, GITHUB_TOKEN, NPM_TOKEN, PUBLISH, REGISTRY_PASSWORD, SOURCE, SSH_PRIVATE_KEY, TARGET, TELEMETRY } from "./config";
import * as M from "./models";

export * from "./config";

const REGISTRY_SECRETS = [GHCR_TOKEN, REGISTRY_PASSWORD];

export const source = steps.gitHubSource({
  name: "source",
  description: "Fetches the pushed commit from GitHub.",
  token: GITHUB_TOKEN,
  apiUrl: SOURCE.apiUrl,
});

export const buildServer = steps.imageBuild({
  name: "build-server",
  description: "Builds the server image (API, frontend, docs) with kaniko and pushes it under this run's version tag.",
  repository: PUBLISH.server.repository,
  push: true,
  cacheRepo: PUBLISH.server.cacheRepo,
  registryMap: PUBLISH.server.registryMap,
  insecureRegistry: PUBLISH.server.insecure,
  registryAuth: PUBLISH.server.auth,
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 1800,
});

export const buildHarness = steps.imageBuild({
  name: "build-harness",
  description: "Builds the step pods' init image (harness/base) when its content changed, under its content tag.",
  repository: PUBLISH.harness.repository,
  context: "harness/base",
  push: true,
  contentTag: true,
  enabled: PUBLISH.harness.push,
  insecureRegistry: PUBLISH.harness.insecure,
  registryAuth: PUBLISH.harness.auth,
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 600,
});

export const test = step({
  name: "test",
  description: "Runs the .NET unit tests and checks api.json, the TS client and the API reference match the API.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  timeoutSeconds: 1800,
});

export const frontendLint = step({
  name: "frontend-lint",
  description: "Lints the frontend and checks its formatting (the server image builds and type-checks it).",
  image: "node:22-slim",
  input: { source: Source },
  timeoutSeconds: 900,
});

export const buildSdk = step({
  name: "build-sdk",
  description: "Checks sdk/ (tsc, compile-fail cases, bun test) and packs it.",
  image: "oven/bun:1.4.3-slim",
  input: { source: Source },
  output: M.SdkPackage,
  timeoutSeconds: 1200,
});

export const buildCli = step({
  name: "build-cli",
  description: "Builds the pl CLI for Linux and Windows.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  output: M.CliRelease,
  timeoutSeconds: 1800,
});

export const publishImages = step({
  name: "publish-images",
  description: "Tags this run's server and harness images as the prerelease (same digests).",
  image: "alpine:3.22",
  input: { server: PushedImage, harness: optional(PushedImage) },
  config: [PUBLISH],
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 300,
});

export const publishSdk = step({
  name: "publish-sdk",
  description: "Publishes the packed SDK to npm under the prerelease dist-tag, when npm publishing is on.",
  image: "oven/bun:1.4.3-slim",
  input: { pkg: M.SdkPackage },
  output: M.SdkRelease,
  config: [PUBLISH],
  secrets: [NPM_TOKEN, ...REGISTRY_SECRETS],
  timeoutSeconds: 600,
});

export const publishCli = step({
  name: "publish-cli",
  description: "Releases this run's pl build: the instance serves it at /download once the run is promoted.",
  image: "alpine:3.22",
  input: { build: M.CliRelease },
  output: M.CliRelease,
  config: [PUBLISH],
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 300,
});

/** What both deploy steps take; the target and how it is reached come from the select case. */
const deployOptions = (target: { auth: ConfigRef; telemetry: ConfigRef }) => ({
  chart: "helm",
  inlineValues: [{ auth: AUTH, telemetry: TELEMETRY }, { auth: target.auth, telemetry: target.telemetry }],
  images: { server: "image" },
  imageRefs: { harness: "harness.image" },
  rolloutTimeoutSeconds: 300,
  healthPath: "/api/health",
  timeoutSeconds: 900,
}) as const;
const describeDeploy = (stage: string) => `Deploys this run's images with Helm to the ${stage} target and checks its health.`;

export const betaTest = step({
  name: "beta-test",
  description: "Smoke-tests the freshly deployed beta: readiness, the API, the frontend bundle, the error model and SSE.",
  image: "alpine:3.22",
  input: { release: HelmRelease },
  config: [AUTH],
  timeoutSeconds: 180,
});

export const promote = step({
  name: "promote",
  description: "Moves the release tags to this run's artifacts: the image digests and the npm version (no rebuild).",
  image: "alpine:3.22",
  input: { server: PushedImage, harness: optional(PushedImage), sdk: optional(M.SdkRelease) },
  config: [PUBLISH],
  secrets: [NPM_TOKEN, ...REGISTRY_SECRETS],
  timeoutSeconds: 300,
});


export default pipeline({
  name: "Olve.Pipelines v2",
  description: "Build and verify the server image, harness image, SDK, CLI and frontend once; publish them as prereleases; deploy beta if one is configured (and smoke-test a helm beta); deploy prod; promote the same artifacts. Targets come from config (helm in-cluster, helm over SSH, or a Docker Compose deployer). deployTo=beta stops after beta.",
  input: M.DeployRequest,
})
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: "prod" }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(buildServer, buildHarness, test, frontendLint, buildSdk, buildCli)
  .stage([publishImages, from => ({ server: from["build-server"], harness: from["build-harness"] })], publishSdk, publishCli)
  .select(TARGET.beta, {
    ssh: (p, beta) => p
      .stage([
        steps.helmDeployOverSsh({ name: "deploy-beta", description: describeDeploy("beta"), target: beta, sshKey: SSH_PRIVATE_KEY, ...deployOptions(beta) }),
        from => ({ source: from.source, server: from["build-server"], harness: from["build-harness"] }),
      ])
      .stage(betaTest),
    inCluster: (p, beta) => p
      .stage([
        steps.helmDeployInCluster({ name: "deploy-beta", description: describeDeploy("beta"), target: beta, ...deployOptions(beta) }),
        from => ({ source: from.source, server: from["build-server"], harness: from["build-harness"] }),
      ])
      .stage(betaTest),
    compose: (p, beta) => p.stage([
      steps.composeDeploy({ name: "deploy-beta", description: describeDeploy("beta"), target: beta, token: DEPLOYER_TOKEN, timeoutSeconds: 900 }),
      from => ({ image: from["build-server"] }),
    ]),
    none: p => p,
  })
  .when({ deployTo: "prod" }, p => p
    .select(TARGET.prod, {
      ssh: (q, prod) => q.stage([
        steps.helmDeployOverSsh({ name: "deploy-prod", description: describeDeploy("prod"), target: prod, sshKey: SSH_PRIVATE_KEY, ...deployOptions(prod) }),
        from => ({ source: from.source, server: from["build-server"], harness: from["build-harness"] }),
      ]),
      inCluster: (q, prod) => q.stage([
        steps.helmDeployInCluster({ name: "deploy-prod", description: describeDeploy("prod"), target: prod, ...deployOptions(prod) }),
        from => ({ source: from.source, server: from["build-server"], harness: from["build-harness"] }),
      ]),
      compose: (q, prod) => q.stage([
        steps.composeDeploy({ name: "deploy-prod", description: describeDeploy("prod"), target: prod, token: DEPLOYER_TOKEN, timeoutSeconds: 900 }),
        from => ({ image: from["build-server"] }),
      ]),
    })
    .stage([promote, from => ({ server: from["build-server"], harness: from["build-harness"], sdk: from["publish-sdk"] })]));
