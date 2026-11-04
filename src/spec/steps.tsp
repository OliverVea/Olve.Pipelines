import "@typespec/http";
import "./errors.tsp";
import "./runs.tsp";

using Http;

namespace Pipelines;

/** One attempt of a step, across runs. `stepVersionChanged` marks the first row after the step's definition changed. */
model StepHistoryItem {
  runNumber: int32;
  runStatus: RunStatus;
  versionNumber: int32;
  versionStamp: string;
  @format("uuid") attemptId: string;
  attemptNumber: int32;
  status: AttemptState;
  reason: string | null;
  createdAt: utcDateTime;
  startedAt: utcDateTime | null;
  finishedAt: utcDateTime | null;
  stepVersion: int32 | null;
  stepVersionChanged: boolean;
  phases?: AttemptPhases | null;
  links?: AttemptLink[] | null;
}

/** The runs the report covers: the latest `requestedRuns`, or fewer when no more exist. `from` and `to` are when the first and last covered runs started. */
model ReliabilityWindow {
  fromRun: int32;
  toRun: int32;
  runs: int32;
  requestedRuns: int32;
  from: utcDateTime | null;
  to: utcDateTime | null;

  /** Fewer runs than requested, because older ones were deleted. */
  limitedByRetention: boolean;
}

/** Of the settled (succeeded or failed) runs, those stopped by a flaky or infrastructure failure, a rerun having rescued them or not. A flaky failure nobody reran stays unseen, so this is a lower bound. */
model ReliabilityRuns {
  settled: int32;
  failedForNoReason: int32;

  /** `failedForNoReason` over `settled`: the chance a run fails for no real reason. */
  rate: float64;

  /** Other settled runs where such a failure happened and automatic retries covered it. */
  absorbed: int32;

  /** The runs counted in `failedForNoReason`, newest first. */
  runNumbers: int32[];
}

/** One step's attempts that reached an outcome, succeeded or failed; a declined attempt or one the scheduler failed without running is neither. */
model StepReliability {
  step: string;
  attempts: int32;

  /** `ownFailures` + `infrastructureFailures` + `timeouts`. */
  failures: int32;

  /** The step ran and reported a failure, which may be a real problem it found. */
  ownFailures: int32;

  /** The platform around the step failed: pod, image pull, launch, storage. */
  infrastructureFailures: int32;

  timeouts: int32;
  failureRate: float64;

  /** Over the latest 10 attempts; null with fewer than 20 attempts. */
  recentFailureRate: float64 | null;

  /** Over the attempts before those; null with fewer than 20 attempts. */
  usualFailureRate: float64 | null;

  /** At least 3 of the latest 10 attempts failed, and their rate is at least 25 points above the step's usual rate. */
  aboveUsual: boolean;

  /** Own failures where the same code, config and input also succeeded. */
  flakyFailures: int32;

  /** Own failures where the same code, config and input failed again and never succeeded. */
  reproducedFailures: int32;

  /** Own failures whose code, config and input never ran again. */
  unverifiedFailures: int32;

  /** `flakyFailures` over `attempts`. */
  flakyRate: float64;

  /** The runs with a flaky failure, newest first. */
  flakyRuns: int32[];
}

/** How reliable a pipeline's steps were over its latest runs. Steps above their usual failure rate come first, then the flakiest. */
model ReliabilityReport {
  window: ReliabilityWindow;
  runs: ReliabilityRuns;
  steps: StepReliability[];

  /** What the report couldn't work out. */
  notes: string[];
}

@tag("Steps")
@route("/api/pipelines/{pipelineId}")
interface Steps {
  /** The blocked step names: an operator hold, keyed on step name and kept across versions. */
  @summary("List blocked steps")
  @get @route("blocked-steps") blocked(@path @format("uuid") pipelineId: string): string[] | NotFound;

  /** Runs that reach the step are held there until it is unblocked. Answers the blocked step names. */
  @summary("Block a step")
  @post @route("steps/{step}/block") block(@path @format("uuid") pipelineId: string, @path step: string): string[] | NotFound | BadRequest;

  /** Held runs resume. Answers the blocked step names. */
  @summary("Unblock a step")
  @post @route("steps/{step}/unblock") unblock(@path @format("uuid") pipelineId: string, @path step: string): string[] | NotFound | BadRequest;

  /** The step's attempts across runs, newest first, with markers where the step's definition changed. */
  @summary("Get a step's history")
  @get @route("steps/{step}/history") history(
    @path @format("uuid") pipelineId: string,
    @path step: string,
    @query limit?: int32,
  ): StepHistoryItem[] | NotFound;

  /**
   * `runs` is how many of the latest runs to cover (default 100, at most 500); retention limits how far back they
   * reach, and `window` says what is covered. A high failure rate is not a defect: a test step fails on real problems.
   */
  @summary("Get the reliability of a pipeline's steps")
  @get @route("reliability") reliability(
    @path @format("uuid") pipelineId: string,
    @query runs?: int32,
  ): ReliabilityReport | NotFound | BadRequest;
}
