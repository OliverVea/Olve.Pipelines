// Olve.Pipelines deploying itself: build and verify every component once, publish prereleases,
// deploy beta, smoke-test it, deploy prod, then promote the same artifacts. Where it all goes is the
// instance's config (config.ts).
import { pipeline, steps, type ConfigRef } from '@olivervea/pipelines';
import {
  AUTH,
  DEPLOYER_TOKEN,
  GITHUB_TOKEN,
  PUBLISH,
  REGISTRY_SECRETS,
  SOURCE,
  SSH_PRIVATE_KEY,
  TARGET,
  TELEMETRY,
} from './config';
import * as M from './models';
import { betaTest } from './steps/beta-test';
import { buildCli } from './steps/build-cli';
import { buildFrontend } from './steps/build-frontend';
import { buildSdk } from './steps/build-sdk';
import { buildServer } from './steps/build-server';
import { compileServer } from './steps/compile-server';
import { promote } from './steps/promote';
import { publishCli } from './steps/publish-cli';
import { publishImages } from './steps/publish-images';
import { publishSdk } from './steps/publish-sdk';
import { spec } from './steps/spec';
import { test } from './steps/test';

const source = steps.gitHubSource({
  name: 'source',
  description: 'Fetches the pushed commit from GitHub.',
  token: GITHUB_TOKEN,
  apiUrl: SOURCE.apiUrl,
});

const buildHarness = steps.imageBuild({
  name: 'build-harness',
  description:
    "Builds the step pods' init image (src/harness/base) when its content changed, under its content tag.",
  repository: PUBLISH.harness.repository,
  context: 'src/harness/base',
  push: true,
  contentTag: true,
  enabled: PUBLISH.harness.push,
  insecureRegistry: PUBLISH.harness.insecure,
  registryMap: PUBLISH.harness.registryMap,
  registryAuth: PUBLISH.harness.auth,
  secrets: REGISTRY_SECRETS,
  timeoutSeconds: 600,
});

/** What both deploy steps take; the target and how it is reached come from the select case. */
const deployOptions = (target: { auth: ConfigRef; telemetry: ConfigRef }) =>
  ({
    chart: 'src/deploy/helm',
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

export default pipeline({
  name: 'Olve.Pipelines v2',
  description:
    'Build and verify the server image, harness image, SDK, CLI and frontend once; publish them as prereleases; deploy beta if one is configured (and smoke-test a helm beta); deploy prod; promote the same artifacts. Targets come from config (helm in-cluster, helm over SSH, or a Docker Compose deployer). deployTo=beta stops after beta.',
  input: M.DeployRequest,
  retention: { keepLatestRuns: 50, minRunAgeDays: 30 },
})
  .onPush((e) => ({ repo: e.repository.full_name, sha: e.after, deployTo: 'prod' }))
  .stage([source, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(spec)
  .stage(compileServer, buildFrontend, buildHarness, test, buildSdk, buildCli)
  .stage(buildServer, publishSdk, publishCli)
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
