import "@typespec/http";
import "./errors.tsp";
import "./pipelines.tsp";

using Http;

namespace Pipelines;

/** The outcome of the last reconcile. */
enum ReconcileResult {
  NeverRun,
  Success,
  Error,
}

model ReconcileStatus {
  result: ReconcileResult;
  lastSyncTime: utcDateTime | null;
  problems: string[];
  warnings: string[];

  /** The config commit the last reconcile attempt was for. */
  sha: string | null;

  inProgress: boolean;

  /** The version the last successful reconcile switched to. */
  versionNumber: int32 | null;
}

/** A pipeline's GitOps binding: the repo, branch and path it reconciles its definition from. The webhook's HMAC key never leaves the service. */
model PipelineBinding {
  @format("uuid") id: string;
  @format("uuid") pipelineId: string;
  repo: string;
  branch: string;
  path: string;

  /** The pipeline-secret key holding a GitHub token, for the authenticated rate limit. */
  credentialsSecret: string | null;

  lastPushSha: string | null;
  lastReconciledSha: string | null;
  status: ReconcileStatus;
  createdAt: utcDateTime;

  /** Off, the binding syncs only on reconcile-now or its webhook. */
  polls: boolean;
}

/** The last reconcile's result and commit, whether one is in progress, the resulting and the active version. */
model PipelineBindingStatus {
  @format("uuid") pipelineId: string;
  repo: string;
  branch: string;
  path: string;
  lastPushSha: string | null;
  lastReconciledSha: string | null;
  result: ReconcileResult;
  lastSyncTime: utcDateTime | null;
  problems: string[];
  warnings: string[];

  /** The config commit the last reconcile attempt was for. */
  reconcileSha: string | null;

  reconcileInProgress: boolean;

  /** The version the last successful reconcile switched to. */
  reconciledVersion: int32 | null;

  activation: PipelineActivation | null;

  /** A version reconcile produced that waits for its config or secrets, with the reasons. */
  pending?: PendingVersionInfo | null;
}

/** Creates a pipeline bound to a repo: the only way to create one. */
model CreatePipeline {
  /** `owner/name`. */
  repo: string;

  /** Default `main`. */
  branch?: string | null;

  /** The directory of the pipeline definition; default `.pipelines`. */
  path?: string | null;

  credentialsSecret?: string | null;
}

model UpdateBinding {
  /** Null clears it. */
  credentialsSecret: string | null;
}

model SetBindingBranch {
  branch: string;
}

model SetBindingPolling {
  polls: boolean;
}

model Created<T> {
  @statusCode statusCode: 201;
  @body body: T;
}

@tag("Bindings")
@route("/api/pipelines")
interface Bindings {
  /** Creates a pipeline bound to `repo`; the binding watches `path` on `branch`. A failed bind leaves no pipeline behind. */
  @summary("Create a pipeline bound to a repo")
  @post create(@body body: CreatePipeline): Created<PipelineBinding> | BadRequest | Conflict;

  @summary("Get the binding")
  @get @route("{pipelineId}/binding") get(@path @format("uuid") pipelineId: string): PipelineBinding | NotFound;

  @summary("Get the binding's reconcile status")
  @get @route("{pipelineId}/binding/status") status(@path @format("uuid") pipelineId: string): PipelineBindingStatus | NotFound;

  /** Sets or clears the credentials secret key. */
  @summary("Update the binding")
  @patch(#{ implicitOptionality: false }) @route("{pipelineId}/binding") update(
    @path @format("uuid") pipelineId: string,
    @body body: UpdateBinding,
  ): PipelineBinding | NotFound | BadRequest;

  /** Binds the pipeline to another branch of the same repo, keeping its runs, versions and secrets. */
  @summary("Move the binding to another branch")
  @put @route("{pipelineId}/binding/branch") setBranch(
    @path @format("uuid") pipelineId: string,
    @body body: SetBindingBranch,
  ): PipelineBinding | NotFound | BadRequest;

  /** Off, the binding no longer polls GitHub; it syncs only on reconcile-now or its webhook. */
  @summary("Turn the binding's polling on or off")
  @put @route("{pipelineId}/binding/polling") setPolling(
    @path @format("uuid") pipelineId: string,
    @body body: SetBindingPolling,
  ): PipelineBinding | NotFound;

  /** Queues a reconcile of the bound config and returns at once; also retries a failed commit. `rebuild` reconciles the current commit again. `wait` returns once it finished: 409 with the problems when it failed. */
  @summary("Reconcile now")
  @post @route("{pipelineId}/binding/reconcile") reconcile(
    @path @format("uuid") pipelineId: string,
    @query wait?: boolean,
    @query rebuild?: boolean,
  ): OkResponse | NotFound | BadRequest | Conflict;
}
