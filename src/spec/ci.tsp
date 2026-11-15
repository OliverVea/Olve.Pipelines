import "@typespec/http";
import "@typespec/sse";
import "./errors.tsp";
import "./events.tsp";
import "./runs.tsp";

using Http;
using SSE;

namespace Pipelines;

enum PullState {
  open,
  merged,
  closed,
}

/** `pull`: one pull request merged onto `main`. `queue`: a merge-queue batch, its pull requests merged onto `main` in queue order. `close`: removes a pull request's preview once it closed or merged, with the definition of the run that deployed it; it reports no `olve/ci`. */
enum CiRunKind {
  pull,
  queue,
  close,
}

/** `failing`: its close run failed and is tried again. */
enum PreviewState {
  deploying,
  live,
  removing,
  removed,
  failing,
}

/** A pull request's preview: the live instance its CI runs deploy, removed when it closes or merges. */
model Preview {
  state: PreviewState;

  /** The preview's link, from the step that deployed it; null until a step links it. */
  url: string | null;

  /** The CI run that last deployed it. */
  deployedBy: int32;

  deployedAt: utcDateTime;
}

/** Why the service cancelled a CI run: the pull request closed or merged, or left the queue. */
enum CiCancelReason {
  closed,
  merged,
  dequeued,
}

/** A step of a CI run in the list's step strip: its stage and its status. */
model CiStepState {
  name: string;
  stage: int32;
  status: RunStepStatus;
}

/** A CI run as listed. CI runs are numbered per pipeline in a sequence of their own. */
model CiRunSummary {
  @format("uuid") id: string;
  number: int32;
  kind: CiRunKind;
  status: RunStatus;

  /** The pull requests it tests: one for a `pull` run, the batch in queue order for a `queue` run. */
  pulls: int32[];

  /** The commit tested: GitHub's merge commit of the pull request, or the head of the queue's batch branch. */
  testedSha: string;

  /** The tree of `testedSha`; succeeded steps are reused across runs of the same tree. */
  tree: string;

  /** The `main` commit it was tested on. */
  mainSha: string;

  createdAt: utcDateTime;
  finishedAt: utcDateTime | null;
  supersededByNumber: int32 | null;
  cancelReason: CiCancelReason | null;
  steps: CiStepState[];
}

/** A pull request a CI run tests, with the head it was tested at. */
model CiRunPull {
  number: int32;
  title: string;
  branch: string;
  headSha: string;
}

/** A CI attempt another one stands in for: the run, step (or cell) and attempt it reuses. */
model CiAttemptRef {
  run: int32;
  step: string;
  attempt: int32;
}

/** One step of a CI run: as for a CD run, and `reusedFrom` when a succeeded attempt on the same tree stands in for it. */
model CiRunStep {
  name: string;
  status: RunStepStatus;
  reason: string | null;
  attemptCount: int32;
  latestAttempt: int32 | null;
  succeededAttempt: int32 | null;
  startedAt: utcDateTime | null;
  finishedAt: utcDateTime | null;
  phases?: AttemptPhases | null;
  links?: AttemptLink[] | null;
  reusedFrom: CiAttemptRef | null;
  cells?: CiRunStepCell[] | null;
}

model CiRunStepCell {
  ...RunStepCell;
  reusedFrom: CiAttemptRef | null;
}

model CiRunStage {
  index: int32;
  when: RunStageCondition[];
  steps: CiRunStep[];
}

/** A CI run: what it tests, which definition it ran, and its stages with each step's latest attempt. */
model CiRun {
  ...CiRunSummary;
  pullHeads: CiRunPull[];

  /** The commit whose definition the run uses: `mainSha` when the tested tree leaves the definition as on `main`, else `testedSha` (a build of its own). */
  definitionSha: string;

  /** Empty until the definition is built and described; a run whose definition fails says why in `problems`. */
  stages: CiRunStage[];

  problems: string[];
}

/** One execution of one step of a CI run; a reused attempt ran no Job and points at the attempt it reuses. */
model CiAttempt {
  @format("uuid") id: string;
  runNumber: int32;
  stepName: string;
  number: int32;
  status: AttemptStatus;
  createdAt: utcDateTime;
  output?: AttemptOutput | null;

  /** The assembled `input.json`. */
  input?: string | null;

  timings?: AttemptTimings | null;
  links?: AttemptLink[] | null;
  reusedFrom: CiAttemptRef | null;
}

enum QueueEntryState {
  queued,
  testing,
  merged,
  removed,
}

/** How a pull request entered the queue: through the API or by GitHub auto-merge being turned on. */
enum QueueEntryVia {
  api,
  `auto-merge`: "auto-merge",
}

/** A pull request's place in the merge queue. */
model QueueEntry {
  pull: int32;
  title: string;
  branch: string;

  /** The head it was enqueued at; a push to the pull request removes it from the queue. */
  headSha: string;

  state: QueueEntryState;

  /** 1-based among the entries not yet finished; null once merged or removed. */
  position: int32 | null;

  enqueuedAt: utcDateTime;
  via: QueueEntryVia;

  /** Who asked, for an entry enqueued through the API. */
  enqueuedBy: string | null;

  /** The queue runs that tested it, newest first. */
  runs: int32[];

  finishedAt: utcDateTime | null;

  /** For a removed entry: why (`test failed`, `pushed`, `closed`, `dequeued by <login>`, `conflict`). */
  reason: string | null;

  /** For an entry removed by a failed run: the failed step. */
  failedStep: string | null;
}

/** The merge queue: entries still to merge in order, then those merged or removed in the last day, newest first. */
model Queue {
  entries: QueueEntry[];
  finished: QueueEntry[];
}

/** A pull request into `main` and its CI. */
model Pull {
  number: int32;
  title: string;
  branch: string;
  headSha: string;
  draft: boolean;
  state: PullState;

  /** GitHub can't merge it onto `main`: it gets no run until a push lets it merge. */
  conflict: boolean;

  /** The latest run was tested on a `main` that has moved since. */
  stale: boolean;

  latestRun: CiRunSummary | null;
  queue: QueueEntry | null;

  /** Null when the pull request never had one. */
  preview: Preview | null;

  updatedAt: utcDateTime;
}

/** Pull requests with their latest CI run: open ones newest first, or closed and merged ones newest first, `limit` from `offset` out of `total`. */
model PullList {
  items: Pull[];
  offset: int32;
  limit: int32;
  total: int32;
}

/** A pull request and every CI run of it, newest first (queue and close runs included). */
model PullDetail {
  ...Pull;
  runs: CiRunSummary[];
}

model EnqueuePull {
  pull: int32;
}

@tag("CI")
@route("/api/pipelines/{pipelineId}/ci/pulls")
interface CiPulls {
  @summary("List pull requests")
  @get list(
    @path @format("uuid") pipelineId: string,
    @query state?: "open" | "closed",
    @query offset?: int32,
    @query limit?: int32,
  ): PullList | NotFound;

  @summary("Get a pull request")
  @get @route("{pull}") get(@path @format("uuid") pipelineId: string, @path pull: int32): PullDetail | NotFound;

  /** Runs the pull request's head again; steps that already succeeded on the same tree are reused, so only the rest run. Supersedes its running run. */
  @summary("Run a pull request again")
  @post @route("{pull}/runs") run(
    @path @format("uuid") pipelineId: string,
    @path pull: int32,
  ): CiRun | NotFound | BadRequest | Conflict | ServiceUnavailable;
}

@tag("CI")
@route("/api/pipelines/{pipelineId}/ci/runs")
interface CiRuns {
  @summary("Get a CI run")
  @get @route("{number}") get(@path @format("uuid") pipelineId: string, @path number: int32): CiRun | NotFound;

  /** Cancels the CI run and its scheduled and in-progress attempts (their Jobs are deleted). Cancelling a queue run removes its pull requests from the queue. */
  @summary("Cancel a CI run")
  @post @route("{number}/cancel") cancel(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
  ): CiRun | NotFound | Conflict | ServiceUnavailable;

  @summary("List a CI run's attempts")
  @get @route("{number}/attempts") attempts(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
  ): CiAttempt[] | NotFound;
}

/** `step` is a step's name or a matrix cell's (URL-encoded). A reused attempt's output, files and log are those of the attempt it reuses. */
@tag("CI")
@route("/api/pipelines/{pipelineId}/ci/runs/{number}/steps/{step}")
interface CiRunSteps {
  /** The files an attempt stored (default: the latest successful attempt). */
  @summary("List a CI step's files")
  @get @route("files") files(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): StepFiles | NotFound;

  #suppress "@typespec/openapi3/path-reserved-expansion" "Served as a catch-all: a file path spans segments"
  /** One stored file of an attempt (default: the latest successful attempt); a directory `dir/` downloads as `dir.tar`. */
  @summary("Download a CI step file")
  @get @route("files/{+path}") download(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @path path: string,
    @query attempt?: int32,
  ): StepFileContent | NotFound | BadRequest;

  /** The output JSON of an attempt (default: the latest successful attempt). */
  @summary("Get a CI step's output")
  @get @route("output") output(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): StepOutput | NotFound;

  /** The attempt's log (default: the latest attempt); the live pod log while it runs. */
  @summary("Get a CI step's log")
  @get @route("logs") logs(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): StepLog | NotFound;

  /** The attempt's log as server-sent events (default: the latest attempt): `log` chunks, then `end` once the attempt is finished. */
  @summary("Stream a CI step's log")
  @get @route("logs/stream") logStream(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @path step: string,
    @query attempt?: int32,
  ): SSEStream<LogEvent> | NotFound;
}

@tag("CI")
@route("/api/pipelines/{pipelineId}/ci/queue")
interface CiQueue {
  @summary("Get the merge queue")
  @get get(@path @format("uuid") pipelineId: string): Queue | NotFound;

  /** Adds an open pull request into `main` to the merge queue. Refused for a draft, a conflicting pull request and one from a fork; the binding's token must be able to write. */
  @summary("Enqueue a pull request")
  @post enqueue(
    @path @format("uuid") pipelineId: string,
    @body body: EnqueuePull,
  ): QueueEntry | NotFound | BadRequest | Conflict | ServiceUnavailable;

  /** Takes the pull request out of the queue; a queue run testing it is cancelled and the rest of its batch queued again. */
  @summary("Dequeue a pull request")
  @delete @route("{pull}") dequeue(
    @path @format("uuid") pipelineId: string,
    @path pull: int32,
  ): QueueEntry | NotFound | Conflict | ServiceUnavailable;
}
