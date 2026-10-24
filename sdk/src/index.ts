/// <reference path="./assets.d.ts" />
// @olivervea/pipelines — authoring API (contracts, handlers, pipeline builder).
export { z } from "zod";
export { model, fileRef, optional, type Model, type AnyModel, type Infer, type InferInput, type Plain, type FileRef, type Optional } from "./core/models.ts";
export { secret, type Secret, type DeclaredSecrets } from "./core/secrets.ts";
export { config, secretRef, type ConfigEntry, type AnyConfigEntry, type ConfigValues } from "./core/config.ts";
export { hmacSha256, githubSignature, bearer, type WebhookVerify, type WebhookEvent } from "./core/webhooks.ts";
export { step, type Step, type AnyStep, type StepDef, type StepInput, type StepKind, type ConfigOf, type InputEntry, type InputSpec, type LibraryStep, type OutputValue } from "./core/steps.ts";
export { implement, type Implementation, type Handler, type HandlerResult, type Skipped } from "./core/handlers.ts";
export type { Ctx, RunInfo, Sh, ShOptions, ExecOptions, Http, HttpInit, RetryOptions, OutputFileOptions } from "./ctx/types.ts";
export type { Ref, Mappable, MapperResult } from "./builder/refs.ts";
export { pipeline, Pipeline, type PipelineDef, type InRefs, type InitialAvail, type WhenCond, type PushEvent, type StageEntry, type CheckStage, type MapperFn } from "./builder/pipeline.ts";
export { steps, Source, BuiltImage, PushedImage, HelmRelease, FailureContext, type RunValue, type ScriptDef, type GitHubSourceDef, type ImageBuildDef, type ImageBuildTarballDef, type ImageBuildPushDef, type RegistryAuth, type HelmDeployOverSshDef, httpHealthCheck, type HttpHealthCheckOptions, sshSession, shq, type SshSession, serviceUrl, checkSpa } from "./library/index.ts";
