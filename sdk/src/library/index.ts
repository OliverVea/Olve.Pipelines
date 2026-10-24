import { gitHubSource } from "./github-source.ts";
import { helmDeployInCluster } from "./helm-deploy.ts";
import { helmDeployOverSsh } from "./helm-deploy-ssh.ts";
import { imageBuild } from "./image-build.ts";
import { script } from "./script.ts";

export { Source, BuiltImage, PushedImage, HelmRelease, FailureContext } from "./models.ts";
export { inputEnvName, type RunValue, type ScriptDef } from "./script.ts";
export type { GitHubSourceDef } from "./github-source.ts";
export { registryTag, registryDigest, type ImageBuildDef, type ImageBuildTarballDef, type ImageBuildPushDef, type RegistryAuth, type RegistryAuthConfig, type RegistryAccess } from "./image-build.ts";
export type { HelmDeployOverSshDef } from "./helm-deploy-ssh.ts";
export { helmTarget, type HelmDeployInClusterDef, type HelmDeploySshTargetDef, type HelmTargetDeployDef, type HelmTargetValue, type InClusterTargetValue, type SshTargetValue } from "./helm-deploy.ts";
export { sshSession, shq, type SshSession } from "./ssh.ts";
export { serviceUrl, checkSpa } from "./checks.ts";
export { httpHealthCheck, HTTP_HEALTH_DEFAULTS, type HttpHealthCheckOptions } from "./http-health.ts";
export { npmDistTag, npmHasVersion, NPM_REGISTRY } from "./npm.ts";

/** SDK-provided steps (step + handler pairs with typed config). */
export const steps = { script, gitHubSource, imageBuild, helmDeployOverSsh, helmDeployInCluster };
