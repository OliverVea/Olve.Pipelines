import "@typespec/http";
import "./errors.tsp";
import "./runs.tsp";

using Http;

namespace Pipelines;

/** A pipeline with its bound repo, its health over runs and its current version's stages. */
model Pipeline {
  @format("uuid") id: string;
  name: string;
  description: string | null;

  /** The bound repository (`owner/name`), if any. */
  repo: string | null;

  health: PipelineHealth;

  /** The current version's barrier stages (the switch target while draining); empty without a version. */
  stages: PipelineStageSummary[];
}

/** One barrier stage with the `when` conditions enclosing it. */
model PipelineStageSummary {
  when: RunStageCondition[];
  steps: PipelineStepSummary[];
}

/** A `when` condition: the input field and the literal values it may take. */
model RunStageCondition {
  field: string;
  values: unknown[];
}

/** A step and its latest attempt across runs (`status` null = never run). */
model PipelineStepSummary {
  name: string;
  status: string | null;
  runNumber: int32 | null;
  at: utcDateTime | null;
}

enum PipelineStatus {
  Healthy: "Healthy",
  RunningHealthy: "Running (Healthy)",
  RunningUnhealthy: "Running (Unhealthy)",
  Unhealthy: "Unhealthy",
  Idle: "Idle",
}

/**
 * Health of a pipeline over its runs: running when any run is Running, else from the latest
 * settled run (cancelled and superseded runs say nothing about health).
 */
model PipelineHealth {
  status: PipelineStatus;

  /** The newest run, whatever its status. */
  lastRun: RunSummary | null;

  /** The newest Succeeded or Failed run. */
  lastSettledRun: RunSummary | null;

  runningRuns: int32;

  /** The active version or the switch in progress (null = no version yet). */
  activation: PipelineActivation | null;

  draining: boolean;
  blockedSteps: string[];

  /** Running runs held at a blocked step. */
  heldRuns: RunSummary[];

  /** A version waiting for its config or secrets (or the active one, held), with the reasons. */
  pending?: PendingVersionInfo | null;
}

/** New runs start on this version. */
model PipelineActivationActive {
  type: "active";
  @format("uuid") versionId: string;
  versionNumber: int32;
  since: utcDateTime;
}

/** A switch in progress: `from` starts no new runs while its in-flight jobs finish (or are cancelled). */
model PipelineActivationDraining {
  type: "draining";
  @format("uuid") fromVersionId: string;
  fromVersionNumber: int32;
  @format("uuid") toVersionId: string;
  toVersionNumber: int32;
  cancelRunning: boolean;
  since: utcDateTime;
}

@discriminated(#{ envelope: "none", discriminatorPropertyName: "type" })
union PipelineActivation {
  active: PipelineActivationActive,
  draining: PipelineActivationDraining,
}

model PendingVersionInfo {
  version: int32;
  since: utcDateTime;

  /** The pending version is the active one, held until its config or secrets are satisfied. */
  held: boolean;

  reasons: string[];
  changedDefaults: ChangedDefault[];
}

/** A config default the pending version changes. */
model ChangedDefault {
  path: string;
  from: string;
  to: string;
}

@tag("Pipelines")
@route("/api/pipelines")
interface Pipelines {
  /** Every pipeline, whole. */
  @summary("List pipelines")
  @get list(): Pipeline[];

  @summary("Get a pipeline")
  @get @route("{pipelineId}") get(@path @format("uuid") pipelineId: string): Pipeline | NotFound;

  /** Deletes the pipeline with its runs, versions, binding and secrets. */
  @summary("Delete a pipeline")
  @delete @route("{pipelineId}") delete(@path @format("uuid") pipelineId: string): OkResponse | NotFound;

  /** A pipeline's health over its runs, its activation, blocked steps and held runs. */
  @summary("Get a pipeline's health")
  @get @route("{pipelineId}/health") health(@path @format("uuid") pipelineId: string): PipelineHealth | NotFound;
}
