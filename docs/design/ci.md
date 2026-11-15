# CI

Status: partly built. Pull requests get CI runs, on the active version's definition and binary or,
for a PR that changes the definition, on a build of its own, report `olve/ci`, and reuse steps that
succeeded on an identical tree; the merge queue tests batches of up to 5, lands them and reports
`olve/merge-queue`; pipeline health counts pull requests. The UI shows the CI tab, CI runs and the
queue, and `pl ci` and `pl wait ci` cover CI runs and the queue (`pl ci merge|dequeue|queue`,
`pl wait ci --queue`). The CI scheduler doesn't apply a matrix step's `cascadeFailure`, and CI
stages can't branch on config (`select`).

CI checks pull requests before they merge, and CD deploys what merged. They are separate: each has
its own runs, scheduling and tab in the UI. What they share is the SDK and the execution layer.

## Shared and separate

- **Shared:** models, `step`, `implement` and library steps. A CI definition stages the same steps
  the pipeline does. Also shared: the build Job and step binary, the harness (`AttemptLaunchSpec`,
  Job factory, result collector, logs, storage layout), pipeline secrets, the K8s executor and the
  Job slots.
- **Separate:** CI runs belong to a pull request, not to a pipeline version, so CD's single active
  version, switch and drain don't apply. CI has its own scheduler. It keeps the stage barrier,
  `when`, skip propagation and `retries`, and leaves out CD's rules across runs: no busy key per
  (pipeline, step), no latest-wins, no superseding of overtaken runs.
- **Attempts:** CI has its own attempt entity, which builds the same `AttemptLaunchSpec`. The
  executor and collector work on the spec, so none of CD's scheduling code changes.

## Which changes get CI

- `main` throughout means the binding's branch. Only pull requests whose base is that branch get
  CI; stacked PRs and branches without a PR get none. There are no other branch or path filters: a
  second target branch is a second binding.
- Draft PRs run at the lowest priority (below).
- Pull requests from forks are ignored.

## Runs of a pull request

- **Trigger:** the binding's GitHub webhook also subscribes to `pull_request`:
  - `opened`, `reopened`, `synchronize`: a run (after the debounce);
  - `ready_for_review`, `converted_to_draft`: the PR's priority;
  - `edited` with a changed base: a PR retargeted away from `main` loses CI and leaves the queue,
    one retargeted to `main` gets a run;
  - `auto_merge_enabled`, `auto_merge_disabled`: enters or leaves the queue;
  - `closed`: cancels the running run and leaves the queue.

  The `push` events the binding already receives tell CI that `main` moved.
- **Debounce:** a run starts once the PR's head has been still for 60 s. A burst of pushes makes one
  run, for the last of them.
- **Supersede at once:** a new run of a PR cancels its running run, Jobs included, through the CI
  equivalent of `RunCancellationService`. The cancelled run is `Superseded` by the new one.
- **Close or merge** cancels the PR's running run.
- **What is tested:** the PR's head merged onto the `main` it was opened or pushed against
  (GitHub's `refs/pull/<n>/merge`). The run records both commits. A PR that doesn't merge cleanly
  gets no run; its status is `conflict` until a push to the PR or to `main` lets it merge.

## When main moves: stale, not rerun

- A newer `main` doesn't rerun open PRs. Their latest result stays, marked **stale**, with the
  `main` it was tested on. Rerunning every open PR on every merge costs a full run per PR per
  merge.
- The guarantee "tested on current main" comes from the merge queue.
- **The checks changed:** when the active version checks PRs differently, the latest result of each
  open, ready PR that used the old checks is outdated, not just stale, and the PR runs again by
  itself. "Differently" is a changed `ci` section or a changed `codeHash`: step code has no hash of its
  own, so any change to the pipeline's code counts. PRs that ran on their own build and drafts are
  left alone.

## Merge queue

- A PR enters the queue when merge is asked for: `POST …/ci/queue`, or GitHub auto-merge. A PR
  with auto-merge on enters when it is turned on, and again on each new head and when it leaves
  draft or reopens, so a fix pushed after a failure goes back in; one removed at the same head stays
  out.
- The queue tests its head entry rebased onto the current `main`, the way the repo merges. On
  success the service merges it through the GitHub API (rebase), naming the head commit it tested.
- **The service merges, not GitHub.** Branch protection on `main` requires `olve/merge-queue`, not
  `olve/ci`. Turning on GitHub auto-merge only enqueues the PR. The service sets `olve/merge-queue`
  to success on the head it tested and merges it, so nothing reaches `main` untested.
- **Landing a batch:** the PRs are merged one by one through the merge API, in queue order, each
  pinned to the head SHA it was tested at. Before each merge `main` has to be at the tree the batch
  had before that PR, and after it at the tree the batch had with it. If either doesn't hold (`main`
  moved outside the queue), the queue stops and the rest of the batch is tested again. The PRs show
  as merged on GitHub. Landing resumes after a restart: a PR GitHub already merged isn't merged again.
- **Batching:** while one entry is tested, others gather. The next test takes up to 5 queued PRs
  merged one on top of the other. Green merges all of them in order. Red splits the batch in
  halves until the failing PR is found; that PR leaves the queue and its author is told through the
  PR's status. The halved limit holds until each PR of the failed batch has merged or left the queue.
- A PR leaves the queue when it is pushed to, closed, retargeted, turned back into a draft, dequeued
  or (when auto-merge queued it) auto-merge is turned off; a run testing it is cancelled and the rest
  of its batch queued again.
- `main` can still move outside the queue (Olve.Trains pushes non-game changes straight to main).
  If it moved while an entry was tested, that entry is tested again on the new `main`.
- Queue runs always fail fast: the first failed step cancels the rest.
- A batch is tested on a temporary branch `olve-queue/<run>`, deleted when the run ends. GitHub's
  API can't rebase, so the service builds the branch from `main` by merging each PR's head in queue
  order (`POST /merges`). Where the PRs apply cleanly that gives the tree the rebase merges give; the
  tree check after each landing catches any difference.

## Priority

`Jobs:MaxConcurrent` stays one budget across CI and CD. Priority is decided per Job, not per run: a
run holds no slot between its steps. When `build` finishes, `test` joins the queue like any other
Scheduled attempt, and a more important attempt that arrived meanwhile goes first. The dispatcher
picks Scheduled attempts in this order:

1. CD runs.
2. Merge-queue runs.
3. PR runs of ready PRs, most recently pushed first: someone is waiting on that one.
4. Reruns of ready PRs because the checks changed.
5. PR runs of draft PRs.

## Reporting to GitHub

The service reports commit statuses from CI run state, with the binding's token:

- `olve/ci` on the PR's head: pending "Waiting to start" as soon as a push arrives, then pending with
  the steps done, success with the time taken, failure naming the failed steps (or the definition's
  problem). A push that may change the definition gets nothing until its own build shows whether it
  keeps CI: a definition without CI must leave no status, and GitHub can't take one back. Deciding
  that takes one compare of the active version's commit with the head per push. A waiting head
  that gets no run says why: error "Doesn't merge onto '<branch>'", or success "No CI to run".
- A run of the current head that is cancelled reports error "Cancelled" (no result; a new run
  replaces it). A superseded run, and one cancelled because its PR closed or merged, report nothing
  more. The status links the run's page (`/pipeline/<id>/ci/run/<n>`).
- Statuses are sent in the background, the latest state per commit, with retries on server errors.
  A token that can't write statuses (403/404) shows as a warning on the binding. At startup the latest
  run of each open PR reports again, in case a status was lost with the process.
- `olve/merge-queue` on the head of a queued PR: pending ("Waiting in the merge queue", "Testing in
  queue run #n"), success ("Passed in queue run #n", set and delivered right before the merge, since
  branch protection requires it), or failure ("Removed: test failed", or why else it left). A token
  that can't write branches or merge (Contents: write) shows as a warning on the binding too.

## Definition

- **Declared** in the pipeline package, usually in a file of its own (`ci.ts`:
  `export const ci = checks({...}).stage(...)`), exported as `ci` from the package's entry point
  next to the default-exported pipeline. `checks()` has no name, input model or triggers; its input
  is `{ repo, sha }`, the tested commit. `describe` emits both into one manifest. The build, the binary, `codeHash` and the
  step definitions are shared, and the one binding covers both.
- **Which definition a run uses:** the PR's own, at the commit it tests. If the PR touches the
  config cursor's paths (the binding path and its local dependencies), that commit gets a build Job
  of its own. Otherwise the run uses the active version's definition and binary. A change to CI is
  tested on itself, and most PRs need no build.
- "Touches" compares the tested tree with the commit the reused binary was built from (the active
  version's), over the cursor's paths. When the active version lags `main` (a failed reconcile, a
  pending config), a PR is then built on its own rather than run on a binary that doesn't match its
  definition. GitHub lists at most 300 changed files; a longer list counts as touching them. A queue
  batch is built once, from its batch commit, if it touches them.
- The build (`CiBuild`, a Job `olve-ci-build-<p12>-<sha12>` with role `ci-build`) runs like
  reconcile's, outside the Job slots, and stores its binary at `versions/<p>/builds/<sha>/`, the
  key reconcile uses for a commit's build: a build is a function of the commit. Runs of the same
  commit share it; a succeeded build whose binary is still stored is reused, a failed one is built
  again. A run waits for it with no attempts; one that ends stops the build if no other run waits.
  A restart reattaches to the Job by name.
- A CI build is a binary and a manifest, nothing more: it never creates a `PipelineVersion`, never
  switches and never changes CD's activation. Its manifest goes through the same
  `ManifestValidator`; a PR whose definition fails describe or validation gets a failed run that
  names the problems.
- **Secrets:** a PR runs its own step code, and same-repo bot branches (Renovate) are not filtered
  like forks are. `checks({ secrets: [...] })` lists the pipeline secrets CI may use. A CI step that
  declares a secret outside the list is a describe problem. Deploy credentials reach PR code only if
  they are listed.
- **Run numbers:** CI runs are numbered per pipeline in a sequence of their own. CD's numbers and
  version stamps keep counting deploys only.

## Reuse by identical tree

A CI run tests a tree: the merge of a PR, or a queue batch rebased onto `main`. When CI meets a
tree it has already tested with the same definition, it doesn't run the passing steps again.

- **Key:** (pipeline, tree SHA, `codeHash`, step or matrix cell). The commit SHAs and the PR are
  left out: rebasing onto the same `main`, amending a message or batching produces new commits but
  the same tree.
- **What is reused:** a succeeded attempt with the same key, with its outputs and files. The new
  run records it as succeeded and links the attempt it came from; no Job runs. Failed, cancelled and
  skipped attempts are never reused, so a red step runs again.
- **Across PR and queue runs:** a PR that was tested on the current `main` and then enters the queue
  alone has nothing left to run, and is merged straight away. A batch half that matches an earlier
  green run is not tested again.
- **The premise:** a CI step's result depends only on the tree and the definition. The source step
  fetches the tested commit, but no step output may depend on which commit with that tree it was.
  The PR number is run metadata, not step input.
- **CI only.** CD never reads CI results (below).

## Without a CI definition

- A pipeline whose entry point exports no `ci` has no CI: no runs, no statuses, no CI tab, and
  `ci` in its health is null. Enqueueing is refused. The binding still keeps track of its pull
  requests, so a PR that adds CI is seen.
- A PR that adds CI touches the definition, so it is built on its own and runs the CI it adds.
- A PR that removes CI gets no run and no `olve/ci` status: the run that waited for its build is
  dropped once the build shows no `ci`. The queue still lands it: its batch's
  definition has no CI steps, so it merges once built and described.
- A PR whose definition doesn't build or describe gets a failed run that lists the problems.

## CD stays self-contained

CD after a merge builds and tests again, even when the queue has just tested the same tree. CD
doesn't read CI results. The overlap is deliberate: it is a safety margin, and CD has to be safe for
changes that never went through CI, such as pushes straight to `main`.

## Entities

- **PullRequest** (pipeline, number): title, head branch, head commit, draft, state (open, merged,
  closed), mergeable, last push time. Kept current from the webhook. `stale` is a query (the latest
  run's `main` against `main`'s head), not stored.
- **CiRun**: numbered per pipeline in CI's own sequence. Kind `pull` (one PR) or `queue` (an ordered
  list of PRs). Records the PR heads, the `main` commit, the tested commit and its tree, and the
  binary it ran (`main`'s or a CI build). Statuses: Scheduled, Running, Succeeded, Failed,
  Cancelled (with the reason: closed, merged, dequeued), Superseded (by a run).
- **CiAttempt**: one step or matrix cell of a CI run; one Job, built through `AttemptLaunchSpec`.
  Statuses like CD's attempts. A reused attempt has no Job and points at the attempt it reuses.
- **QueueEntry** (pipeline, PR): the head it was enqueued at, the time, how it got there (`pl`, UI,
  GitHub auto-merge), state (queued, testing, merged, removed), its runs, and for removed, the
  failed step. A push to a queued PR removes it. Finished entries are listed for a day.
- **CiBuild** (pipeline, tested commit): the build Job and the binary key of a PR or batch whose
  tree touches the cursor's paths.

Persistence follows CD's: `ci-state.json` (PRs, queue, sequences) and `ci/<p>/<run>/run.json` (a
run and its attempts), with step outputs, files and logs under `ci/<p>/<run>/<step>/<attempt>/`.
Retention keeps every run of an open PR and the latest 20 of a closed one, and drops the rest after
183 days.

## API

Pipeline-scoped, under `/api/pipelines/{pipelineId}/ci/`, addressed by PR number and CI run
number:

- `GET pulls?state=open|closed`: PRs with their latest run (status, step strip, commit), stale,
  conflict and queue place. The CI tab's list.
- `GET pulls/{number}`: a PR and its runs.
- `GET runs/{number}`: a CI run: its PRs, commits, `main`, stages and attempts, reused attempts with
  their origin. Logs and files per step as for CD runs (`runs/{number}/steps/{step}/…`).
- `POST runs/{number}/cancel`. `POST pulls/{number}/runs` runs the PR again; with reuse, only steps
  that didn't succeed on this tree run.
- `GET queue`: entries in order with their state and run, plus the day's finished ones.
- `POST queue` `{pull}` enqueues; refused for a draft, a PR not based on `main`, a
  conflicting PR and a fork. `DELETE queue/{pull}` dequeues.
- The pipeline summary gains the counts the homepage tags and the overview's "Pull requests" cell
  show (open, failing, running, queued).
- Change notices on `/api/events` for PRs, CI runs, attempts and the queue, like CD's.

`pl` has `pl ci ls|show|get|cancel|rerun|logs|output|files|file` and `pl wait ci <pipeline> <pr>`
(until the run of the PR's current head ends, following a superseded run), and for the queue
`pl ci merge|dequeue|queue` and `pl wait ci <pipeline> <pr> --queue` (until the PR's latest queue
entry merged or was removed). A CI run's address is `<pipeline>#ci<run>[/<step>[@<attempt>]]`:
its own sequence, apart from the deploy runs' `<pipeline>#<run>`. The `ovea-olve-pipelines` skill
learns them in the same work.

## The binding's token

CI uses the binding's credentials secret. A fine-grained token on the repo needs:

| Permission | Access | For |
|---|---|---|
| Metadata | read | everything |
| Contents | read and write | fetch and compare commits; build and delete `olve-queue/<run>`; merge PRs |
| Pull requests | read | PR state, mergeable, `refs/pull/<n>/merge` |
| Commit statuses | read and write | `olve/ci`, `olve/merge-queue` |
| Webhooks | read and write | the binding hook, with the `pull_request` event added |
| Workflows | write | only if PRs change `.github/workflows/`: pushing the batch branch |

A classic token needs `repo` and `admin:repo_hook`. Without write access the binding still gets PR
runs, but no statuses and no queue; the binding's health says so.
