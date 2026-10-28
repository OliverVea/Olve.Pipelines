import "@typespec/http";
import "./errors.tsp";
import "./events.tsp";
import "./pipelines.tsp";

using Http;
using SSE;

namespace Pipelines;

enum RunStatus {
  Running,
  Succeeded,
  Failed,
  Cancelled,
  Superseded,
}

enum RunTriggerKind {
  Push,
  Poll,
  Manual,
  Webhook,
}

/** A run as listed (no input, no layout). Links to other runs are by number. */
model RunSummary {
  @format("uuid") id: string;
  number: int32;
  status: RunStatus;
  triggerKind: RunTriggerKind;
  versionNumber: int32;
  versionStamp: string;
  createdAt: utcDateTime;
  finishedAt: utcDateTime | null;

  /** The blocked step holding the run, when it is held. */
  heldAt: string | null;

  supersededByNumber: int32 | null;
  rerunOfNumber: int32 | null;
  configSnapshot?: int32 | null;
}

/** A run: its input, pinned version, links, and the pinned version's stage layout with each step's latest attempt. */
model Run {
  ...RunSummary;
  @format("uuid") versionId: string;
  commitSha: string | null;
  input: Record<unknown>;

  /** The run is pinned to the pipeline's active version and not superseded, so it can be continued and its steps rerun. */
  continuable: boolean;

  /** Empty when the pinned version's manifest is unavailable. */
  stages: RunStage[];

  /** The run's effective config (YAML): the pinned snapshot with the version's defaults; null when the version declares none. */
  config?: string | null;
}

/** Runs of a pipeline, newest first: `items` are the `limit` runs from `offset` out of `total`. */
model RunList {
  items: RunSummary[];
  offset: int32;
  limit: int32;
  total: int32;
}

/** One barrier stage of the pinned plan, with the `when` conditions enclosing it (all must match). */
model RunStage {
  index: int32;
  when: RunStageCondition[];
  steps: RunStep[];
}

/**
 * One step of a run. `status` is the latest attempt's status, or without an attempt `held` (the step is
 * blocked and holds the run here), `pending` (not reached yet), `not-run` (the run finished without it)
 * or `excluded` (its `when` didn't match).
 */
model RunStep {
  name: string;
  stepVersion: int32 | null;
  status: string;

  /** The latest attempt's skip or failure reason. */
  reason: string | null;

  attemptCount: int32;
  latestAttempt: int32 | null;

  /** The latest successful attempt: what the address `#run/step` resolves to. */
  succeededAttempt: int32 | null;

  startedAt: utcDateTime | null;
  finishedAt: utcDateTime | null;
  blocked: boolean;
  phases?: AttemptPhases | null;

  /** For a waiting step: the source kinds that may still answer it (`person`, `callback`, `poll`). */
  waitingFor?: string[] | null;
}

/** How long an attempt spent in each phase, in seconds; a phase it never reached is null. */
model AttemptPhases {
  queued: float64;
  podStart: float64 | null;
  fetch: float64 | null;
  image: float64 | null;
  active: float64 | null;
  collect: float64 | null;
  notice?: float64 | null;
}

enum RunStartOutcome {
  /** A new run was created; `run` is it. */
  started: "started",

  /** The same input already runs or just ran; `run` is that run. */
  deduplicated: "deduplicated",

  /** The pipeline is switching versions; the run starts when the switch completes. */
  queued: "queued",
}

/** What starting a run did. */
model RunStart {
  outcome: RunStartOutcome;
  run: Run | null;
}

/** The pipeline-input fields to change, merged over the run's stored input and validated against the pinned input model. */
model ContinueRun {
  input?: Record<unknown> | null;
}

model AttemptStatusWaiting {
  since: utcDateTime;
}

model AttemptStatusScheduled {
  submissionAttempts?: int32;
}

model AttemptStatusInProgress {
  startedAt: utcDateTime;
}

model AttemptStatusSucceeded {
  startedAt: utcDateTime;
  finishedAt: utcDateTime;
}

model AttemptStatusFailed {
  startedAt: utcDateTime;
  finishedAt: utcDateTime;
  reason?: string | null;
}

model AttemptStatusSkipped {
  reason: string;
  startedAt: utcDateTime | null;
  finishedAt: utcDateTime;
}

model AttemptStatusCancelled {
  startedAt: utcDateTime | null;
  cancelledAt: utcDateTime;
}

model AttemptStatusObsolete {
  @format("uuid") supersedingAttemptId: string;
}

/** An attempt's status, discriminated on `type`. */
@discriminated(#{ envelope: "none", discriminatorPropertyName: "type" })
union AttemptStatus {
  waiting: AttemptStatusWaiting,
  scheduled: AttemptStatusScheduled,
  `in-progress`: AttemptStatusInProgress,
  succeeded: AttemptStatusSucceeded,
  failed: AttemptStatusFailed,
  skipped: AttemptStatusSkipped,
  cancelled: AttemptStatusCancelled,
  obsolete: AttemptStatusObsolete,
}

/** A succeeded attempt's output: its storage key and the raw output JSON (size-capped). */
model AttemptOutput {
  key: string;
  json: string;
}

/** When the attempt's pod was created, its init and step containers ran, and the server saw it finish. */
model AttemptTimings {
  podCreatedAt: utcDateTime | null;
  initStartedAt: utcDateTime | null;
  initFinishedAt: utcDateTime | null;
  stepStartedAt: utcDateTime | null;
  stepFinishedAt: utcDateTime | null;
  observedAt?: utcDateTime | null;
}

/** A value given for an external input: the JSON as validated, who gave it, when, and the source kind (`person`, `callback`, `poll`). */
model ExternalAnswer {
  value: string;
  answeredBy: string;
  answeredAt: utcDateTime;
  source?: string;
}

/** One execution of one step: one Kubernetes Job. */
model Attempt {
  @format("uuid") id: string;
  @format("uuid") pipelineId: string;
  @format("uuid") runId: string;
  runNumber: int32;
  stepName: string;
  number: int32;
  status: AttemptStatus;
  createdAt: utcDateTime;
  inputHash?: string | null;
  output?: AttemptOutput | null;

  /** The assembled `input.json`. */
  input?: string | null;

  timings?: AttemptTimings | null;
  answers?: Record<ExternalAnswer> | null;
}

/** An attempt that is waiting, scheduled or in progress, by address. */
model ActiveAttempt {
  @format("uuid") pipelineId: string;
  pipelineName: string;
  run: int32;
  step: string;
  attempt: int32;
  status: string;
  createdAt: utcDateTime;
  startedAt: utcDateTime | null;
}

enum AttemptFilter {
  /** Every waiting, scheduled or in-progress attempt. */
  active,

  /** Only the attempts waiting for external input. */
  waiting,
}

model RerunStep {
  /** The run goes on downstream once the attempt succeeds. */
  continue?: boolean;
}

model AnswerInput {
  value: unknown;

  /** The external input; may be omitted when the step has exactly one. */
  name?: string | null;
}

model DeclineInput {
  comment?: string | null;
}

/** The output of one attempt, addressed `<pipeline>#<run>/<step>[@<attempt>]`. */
model StepOutput {
  run: int32;
  step: string;
  attempt: int32;
  status: string;

  /** Null for a step without an output model. */
  output: unknown | null;

  outputKey: string | null;
}

/** A stored output file of an attempt; `path` is the object path (`dir.tar` for a directory ref `dir/`). */
model StepFile {
  path: string;
  size: int64;
  lastModified: utcDateTime | null;
}

/** The log of one attempt, addressed `<pipeline>#<run>/<step>[@<attempt>]`. */
model StepLog {
  run: int32;
  step: string;
  attempt: int32;
  status: string;

  /** The attempt was finished when the log was read, so `text` is all of it; otherwise the live pod log so far. */
  complete: boolean;

  text: string;
}

model StepFiles {
  run: int32;
  step: string;
  attempt: int32;
  files: StepFile[];
}

/** Per run, each stage's critical step (the one that finished last) and its time; per step, how often it was on the path and its share. */
model CriticalPathReport {
  from: int32;
  to: int32;
  runs: CriticalPathRun[];
  steps: CriticalPathStep[];
  skipped: CriticalPathSkip[];
  totalSeconds: float64;
  averageSeconds: float64;
}

model CriticalPathRun {
  number: int32;
  status: RunStatus;
  versionNumber: int32;
  versionStamp: string;
  seconds: float64;
  idleSeconds: float64;
  path: CriticalPathStage[];
  notes: string[];
  activeSeconds?: float64 | null;
  lead?: CriticalPathLead | null;
}

/** From the trigger event to the run's first attempts, in seconds; null parts are unknown. */
model CriticalPathLead {
  seconds: float64;
  via: string;
  detect: float64 | null;
  check: float64 | null;
  map: float64 | null;
  start: float64 | null;
}

model CriticalPathStage {
  stage: int32;
  step: string;
  attempts: int32;
  seconds: float64;
  waitSeconds: float64;
  activeSeconds?: float64 | null;
}

model CriticalPathStep {
  step: string;
  runs: int32;
  seconds: float64;
  share: float64;
  averageSeconds: float64;
  waitSeconds: float64;
  runNumbers: int32[];
  overheadSeconds?: float64 | null;
}

model CriticalPathSkip {
  number: int32;
  reason: string;
}

@tag("Runs")
@route("/api/pipelines/{pipelineId}/runs")
interface Runs {
  /** Runs of the pipeline, newest first. */
  @summary("List runs")
  @get list(
    @path @format("uuid") pipelineId: string,
    @query offset?: int32,
    @query limit?: int32,
  ): RunList | NotFound;

  /** Starts a manual run; the body is the pipeline input, validated against the input model. Invalid input lists every problem and starts nothing. */
  @summary("Start a run")
  @post start(
    @path @format("uuid") pipelineId: string,
    @body input: Record<unknown>,
  ): RunStart | NotFound | BadRequest | Conflict;

  /**
   * `from` and `to` are run numbers (default: the latest 20 runs, at most 500); without `from`, `sinceVersion`
   * starts at the first run on that pipeline version or a later one. Only succeeded runs unless `includeUnsuccessful`.
   * A stage counts one pass of attempts: automatic retries are included, later reruns are not.
   */
  @summary("Get the critical path of a range of runs")
  @get @route("critical-path") criticalPath(
    @path @format("uuid") pipelineId: string,
    @query from?: int32,
    @query to?: int32,
    @query includeUnsuccessful?: boolean,
    @query sinceVersion?: int32,
  ): CriticalPathReport | NotFound | BadRequest;

  @summary("Get a run")
  @get @route("{number}") get(@path @format("uuid") pipelineId: string, @path number: int32): Run | NotFound;

  /** Cancels the run and its scheduled and in-progress attempts (their Jobs are deleted). */
  @summary("Cancel a run")
  @post @route("{number}/cancel") cancel(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
  ): Run | NotFound | BadRequest | Conflict;

  /** Starts a new run with the same input on the active version (`rerunOfNumber` points back). */
  @summary("Rerun a run")
  @post @route("{number}/rerun") rerun(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
  ): RunStart | NotFound | BadRequest | Conflict;

  /** Continues the run: completed stages are kept, the rest re-evaluates with the changed input. Fields consumed by completed steps can't change; the run's version must be active. */
  @summary("Continue a run")
  @post @route("{number}/continue") continue(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @body body: ContinueRun,
  ): Run | NotFound | BadRequest | Conflict;

  @summary("List a run's attempts")
  @get @route("{number}/attempts") attempts(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
  ): Attempt[] | NotFound;
}

@tag("Steps")
@route("/api/pipelines/{pipelineId}/runs/{number}/steps/{step}")
interface RunSteps {
  @summary("List a step's attempts in a run")
  @get @route("attempts") attempts(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
  ): Attempt[] | NotFound;

  /** A new attempt of the step on the run's pinned version and pool. */
  @summary("Rerun a step")
  @post @route("rerun") rerun(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @body body: RerunStep,
  ): Attempt | NotFound | BadRequest | Conflict;

  /** Cancels the step's scheduled or in-progress attempt (its Job is deleted). The run ends Cancelled once the stage settles. */
  @summary("Cancel a step")
  @post @route("cancel") cancel(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
  ): Attempt | NotFound | BadRequest | Conflict;

  /** Gives the value of an external input of a step that waits for it. The value is validated against the input's model; once every external input is answered the step is scheduled. */
  @summary("Answer a step's external input")
  @post @route("input") answer(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @body body: AnswerInput,
  ): Attempt | NotFound | BadRequest | Conflict;

  /** Fails a step that waits for input without running it; the reason names who declined and the comment. */
  @summary("Decline a step's external input")
  @post @route("input/decline") decline(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @body body: DeclineInput,
  ): Attempt | NotFound | BadRequest | Conflict;

  /** The files an attempt stored (default: the latest successful attempt). */
  @summary("List a step's files")
  @get @route("files") files(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): StepFiles | NotFound;

  /** The output JSON of an attempt (default: the latest successful attempt). */
  @summary("Get a step's output")
  @get @route("output") output(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): StepOutput | NotFound;

  /** The attempt's log (default: the latest attempt); the live pod log while it runs. */
  @summary("Get a step's log")
  @get @route("logs") logs(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): StepLog | NotFound;

  /** The attempt's log as server-sent events (default: the latest attempt): `log` chunks, then `end` once the attempt is finished. */
  @summary("Stream a step's log")
  @get @route("logs/stream") logStream(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): SSEStream<LogEvent> | NotFound;
}

@tag("Attempts")
@route("/api/attempts")
interface Attempts {
  /** What is running (or waiting for input) across pipelines, oldest first. */
  @summary("List running attempts")
  @get list(@query status?: AttemptFilter): ActiveAttempt[];
}
