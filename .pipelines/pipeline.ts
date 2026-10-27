// Olve.Pipelines deploying itself: build and verify every component once, publish prereleases,
// deploy beta, smoke-test it, deploy prod, then promote the same artifacts. Where it all goes is the
// instance's config (config.ts); config.yaml beside it is the paused v1 deployer's rollback path.
import {
  HelmRelease,
  optional,
  pipeline,
  PushedImage,
  Source,
  step,
  steps,
  type ConfigRef,
} from '@olivervea/pipelines';
import {
  AUTH,
  BETA_TOKEN,
  BUILD,
  DEPLOYER_TOKEN,
  GHCR_TOKEN,
  GITHUB_TOKEN,
  NPM_TOKEN,
  PUBLISH,
  REGISTRY_PASSWORD,
  SOURCE,
  SSH_PRIVATE_KEY,
  TARGET,
  TELEMETRY,
} from './config';
import * as M from './models';

export * from './config';

const REGISTRY_SECRETS = [GHCR_TOKEN, REGISTRY_PASSWORD];
const DOTNET_AOT_SDK = 'mcr.microsoft.com/dotnet/sdk:10.0-noble-aot';
const CRANE_IMAGE =
  'gcr.io/go-containerregistry/crane/debug:v0.21.9@sha256:ba318977330d2a41fdd4af7536d35567a4ceb204252b7dfcd19b01dad2567793';

export const source = steps.gitHubSource({
  name: 'source',
  description: 'Fetches the pushed commit from GitHub.',
  token: GITHUB_TOKEN,
  apiUrl: SOURCE.apiUrl,
});

export const compileServer = step({
  name: 'compile-server',
  description:
    'Publishes the server (API and docs) as Native AOT or JIT, per build.runtime, as an image layer.',
  image: DOTNET_AOT_SDK,
  input: { source: Source },
  output: M.ServerBuild,
  config: [BUILD],
  timeoutSeconds: 1800,
});

export const buildFrontend = step({
  name: 'build-frontend',
  description:
    'Lints the frontend, checks its formatting and builds it as an image layer.',
  image: 'node:22-slim',
  input: { source: Source },
  output: M.FrontendBuild,
  timeoutSeconds: 900,
});

export const buildServer = step({
  name: 'build-server',
  description:
    "Puts the server and frontend layers on the .NET base image with crane and pushes it under this run's version tag.",
  image: CRANE_IMAGE,
  input: { server: M.ServerBuild, frontend: M.FrontendBuild },
  output: PushedImage,
  config: [PUBLISH],
  secrets: REGISTRY_SECRETS,
  retries: 2,
  timeoutSeconds: 600,
});

export const buildHarness = steps.imageBuild({
  name: 'build-harness',
  description:
    "Builds the step pods' init image (harness/base) when its content changed, under its content tag.",
  repository: PUBLISH.harness.repository,
  context: 'harness/base',
  push: true,
  contentTag: true,
  enabled: PUBLISH.harness.push,
  insecureRegistry: PUBLISH.harness.insecure,
  registryMap: PUBLISH.harness.registryMap,
  registryAuth: PUBLISH.harness.auth,
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 600,
});

export const test = step({
  name: 'test',
  description:
    'Runs the .NET unit tests and checks api.json, the TS client and the API reference match the API.',
  image: 'mcr.microsoft.com/dotnet/sdk:10.0-noble',
  input: { source: Source },
  timeoutSeconds: 1800,
});

export const buildSdk = step({
  name: 'build-sdk',
  description: 'Checks sdk/ (tsc, compile-fail cases, bun test) and packs it.',
  image: 'oven/bun:1.4.3-slim',
  input: { source: Source },
  output: M.SdkPackage,
  timeoutSeconds: 1200,
});

export const buildCli = step({
  name: 'build-cli',
  description: 'Builds the pl CLI for Linux (Native AOT).',
  image: DOTNET_AOT_SDK,
  input: { source: Source },
  output: M.CliBuild,
  timeoutSeconds: 1800,
});

export const buildCliWindows = step({
  name: 'build-cli-windows',
  description: 'Builds the pl CLI for Windows (self-contained single file).',
  image: 'mcr.microsoft.com/dotnet/sdk:10.0-noble',
  input: { source: Source },
  output: M.CliBuild,
  timeoutSeconds: 1800,
});

export const publishImages = step({
  name: 'publish-images',
  description:
    "Tags this run's server and harness images as the prerelease and copies them to the release repositories (same digests).",
  image: CRANE_IMAGE,
  input: { server: PushedImage, harness: optional(PushedImage) },
  config: [PUBLISH],
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 300,
});

export const publishSdk = step({
  name: 'publish-sdk',
  description:
    'Publishes the packed SDK to npm under the prerelease dist-tag, when npm publishing is on.',
  image: 'oven/bun:1.4.3-slim',
  input: { pkg: M.SdkPackage },
  output: M.SdkRelease,
  config: [PUBLISH],
  secrets: [NPM_TOKEN, ...REGISTRY_SECRETS],
  timeoutSeconds: 600,
});

export const publishCli = step({
  name: 'publish-cli',
  description:
    "Releases this run's pl build: the instance serves it at /download once the run is promoted.",
  image: 'alpine:3.22',
  input: { linux: M.CliBuild, windows: M.CliBuild },
  output: M.CliRelease,
  config: [PUBLISH],
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 300,
});

/** What both deploy steps take; the target and how it is reached come from the select case. */
const deployOptions = (target: { auth: ConfigRef; telemetry: ConfigRef }) =>
  ({
    chart: 'helm',
    inlineValues: [
      { auth: AUTH, telemetry: TELEMETRY },
      { auth: target.auth, telemetry: target.telemetry },
    ],
    images: { server: 'image' },
    imageRefs: { harness: 'harness.image' },
    rolloutTimeoutSeconds: 300,
    healthPath: '/api/health',
    timeoutSeconds: 900,
  }) as const;
const describeDeploy = (stage: string) =>
  `Deploys this run's images with Helm to the ${stage} target and checks its health.`;

export const betaTest = step({
  name: 'beta-test',
  description:
    "Smoke-tests the freshly deployed beta: readiness, the API, the frontend bundle, the error model and SSE; then reconciles and runs beta's test fixture at this commit, whose steps wait for a callback and a poll answered by beta's signal-test service.",
  image: 'alpine:3.22',
  input: { release: HelmRelease, source: Source },
  secrets: [BETA_TOKEN],
  config: [AUTH],
  timeoutSeconds: 900,
});

export const promote = step({
  name: 'promote',
  description:
    "Moves the release tags to this run's artifacts, here and in the release repositories: the image digests, the npm version and a GitHub release with pl (no rebuild).",
  image: CRANE_IMAGE,
  input: {
    source: Source,
    server: PushedImage,
    harness: optional(PushedImage),
    sdk: optional(M.SdkRelease),
    cli: optional(M.CliRelease),
  },
  config: [PUBLISH, SOURCE],
  secrets: [NPM_TOKEN, ...REGISTRY_SECRETS],
  timeoutSeconds: 300,
});

export default pipeline({
  name: 'Olve.Pipelines v2',
  description:
    'Build and verify the server image, harness image, SDK, CLI and frontend once; publish them as prereleases; deploy beta if one is configured (and smoke-test a helm beta); deploy prod; promote the same artifacts. Targets come from config (helm in-cluster, helm over SSH, or a Docker Compose deployer). deployTo=beta stops after beta.',
  input: M.DeployRequest,
  retention: { keepLatestRuns: 50, minRunAgeDays: 30 },
})
  .onPush((e) => ({ repo: e.repository.full_name, sha: e.after, deployTo: 'prod' }))
  .stage([source, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(
    compileServer,
    buildFrontend,
    buildHarness,
    test,
    buildSdk,
    buildCli,
    buildCliWindows,
  )
  .stage(buildServer, publishSdk, [
    publishCli,
    (from) => ({ linux: from['build-cli'], windows: from['build-cli-windows'] }),
  ])
  .select(TARGET.beta, {
    ssh: (p, beta) =>
      p
        .stage(
          [
            steps.helmDeployOverSsh({
              name: 'deploy-beta',
              description: describeDeploy('beta'),
              target: beta,
              sshKey: SSH_PRIVATE_KEY,
              ...deployOptions(beta),
            }),
            (from) => ({
              source: from.source,
              server: from['build-server'],
              harness: from['build-harness'],
            }),
          ],
          [
            publishImages,
            (from) => ({ server: from['build-server'], harness: from['build-harness'] }),
          ],
        )
        .stage(betaTest),
    inCluster: (p, beta) =>
      p
        .stage(
          [
            steps.helmDeployInCluster({
              name: 'deploy-beta',
              description: describeDeploy('beta'),
              target: beta,
              ...deployOptions(beta),
            }),
            (from) => ({
              source: from.source,
              server: from['build-server'],
              harness: from['build-harness'],
            }),
          ],
          [
            publishImages,
            (from) => ({ server: from['build-server'], harness: from['build-harness'] }),
          ],
        )
        .stage(betaTest),
    compose: (p, beta) =>
      p.stage(
        [
          steps.composeDeploy({
            name: 'deploy-beta',
            description: describeDeploy('beta'),
            target: beta,
            token: DEPLOYER_TOKEN,
            timeoutSeconds: 900,
          }),
          (from) => ({ image: from['build-server'] }),
        ],
        [
          publishImages,
          (from) => ({ server: from['build-server'], harness: from['build-harness'] }),
        ],
      ),
    none: (p) =>
      p.stage([
        publishImages,
        (from) => ({ server: from['build-server'], harness: from['build-harness'] }),
      ]),
  })
  .when({ deployTo: 'prod' }, (p) =>
    p
      .select(TARGET.prod, {
        ssh: (q, prod) =>
          q.stage([
            steps.helmDeployOverSsh({
              name: 'deploy-prod',
              description: describeDeploy('prod'),
              target: prod,
              sshKey: SSH_PRIVATE_KEY,
              ...deployOptions(prod),
            }),
            (from) => ({
              source: from.source,
              server: from['build-server'],
              harness: from['build-harness'],
            }),
          ]),
        inCluster: (q, prod) =>
          q.stage([
            steps.helmDeployInCluster({
              name: 'deploy-prod',
              description: describeDeploy('prod'),
              target: prod,
              ...deployOptions(prod),
            }),
            (from) => ({
              source: from.source,
              server: from['build-server'],
              harness: from['build-harness'],
            }),
          ]),
        compose: (q, prod) =>
          q.stage([
            steps.composeDeploy({
              name: 'deploy-prod',
              description: describeDeploy('prod'),
              target: prod,
              token: DEPLOYER_TOKEN,
              timeoutSeconds: 900,
            }),
            (from) => ({ image: from['build-server'] }),
          ]),
      })
      .stage([
        promote,
        (from) => ({
          source: from.source,
          server: from['build-server'],
          harness: from['build-harness'],
          sdk: from['publish-sdk'],
          cli: from['publish-cli'],
        }),
      ]),
  );
