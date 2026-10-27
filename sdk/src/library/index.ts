import { gitHubSource } from './github-source.ts';
import { composeDeploy } from './compose-deploy.ts';
import { helmDeployInCluster } from './helm-deploy.ts';
import { helmDeployOverSsh } from './helm-deploy-ssh.ts';
import { imageBuild } from './image-build.ts';

export {
  Source,
  BuiltImage,
  PushedImage,
  HelmRelease,
  FailureContext,
} from './models.ts';
export type { GitHubSourceDef } from './github-source.ts';
export type {
  ImageBuildDef,
  ImageBuildTarballDef,
  ImageBuildPushDef,
  RegistryAuth,
  RegistryAuthConfig,
} from './image-build.ts';
export type { HelmDeployOverSshDef } from './helm-deploy-ssh.ts';
export {
  type ComposeDeployDef,
  type ComposeTargetValue,
  type ComposeDeployResult,
} from './compose-deploy.ts';
export {
  helmTarget,
  deployTarget,
  type DeployTargetOptions,
  type HelmDeployInClusterDef,
  type HelmDeploySshTargetDef,
  type HelmTargetDeployDef,
  type HelmTargetValue,
  type InClusterTargetValue,
  type SshTargetValue,
} from './helm-deploy.ts';
export { sshSession, type SshSession } from './ssh.ts';
export { serviceUrl, checkSpa } from './checks.ts';
export { httpHealthCheck, type HttpHealthCheckOptions } from './http-health.ts';

/** SDK-provided steps: each is `implement(step(…), …)` with typed settings, staged directly. */
export const steps = {
  gitHubSource,
  imageBuild,
  helmDeployOverSsh,
  helmDeployInCluster,
  composeDeploy,
};
