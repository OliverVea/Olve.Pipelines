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
}
