import "@typespec/http";
import "./errors.tsp";
import "./events.tsp";
import "./runs.tsp";

using Http;

namespace Pipelines;

enum PullState {
  open,
  merged,
  closed,
}

/** `pull`: one pull request merged onto `main`. `queue`: a merge-queue batch, its pull requests merged onto `main` in queue order. */
enum CiRunKind {
  pull,
  queue,
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

enum QueueEntryState {
  queued,
  testing,
  merged,
  removed,
}

/** How a pull request entered the queue: through the API (`pl ci merge`, the UI) or by GitHub auto-merge being turned on. */
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
  updatedAt: utcDateTime;
}

/** Pull requests with their latest CI run: open ones newest first, or closed and merged ones newest first, `limit` from `offset` out of `total`. */
model PullList {
  items: Pull[];
  offset: int32;
  limit: int32;
  total: int32;
}

/** A pull request and every CI run that tested it, newest first (queue runs included). */
model PullDetail {
  ...Pull;
  runs: CiRunSummary[];
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
}