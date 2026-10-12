# Promotion Gate

[← Index](index.md) · [Subject Index](subjects.md)

The promotion gate is how you **pause, release, or retry** the artifact bundle as it advances
into a processing step. It is the one place where operational control overlaps with the
pipeline shape — and it is **state, not config**.

## What a promotion is

A **promotion** is a bundle advancing **into** a processing step. A step's promotions come from
the step **before** it: the bundle that **last succeeded at the previous processing step** — or,
for the first processing step, the **latest completed production run**. Each processing step
has a gate with two controls:

- **Brake** — block / unblock promotion into the step. While blocked, nothing advances into that
  step (and therefore not into any later step, since processing is sequential).
- **Promote** — run the step now with the bundle that last succeeded at the step before it.
  After it succeeds, the chain continues to the next step as usual.

Production steps have **no** gate — only processing steps do.

### Which build Promote uses — and how to see it first

Promote never uses the step's *own* last bundle; it uses what has passed the previous step. Before
you click, every place that offers Promote names that build:

- **UI** — the Promote button (arrow badge in the pipeline flow, labelled button on the step page)
  reads e.g. *"Promote build 2026-10-12 05:49:21 UTC (3f2a9c1e) from test-after-beta"*. When
  there is nothing to promote it is disabled and says why.
- **CLI** — `pl processing list <pipelineId>` has a `PROMOTE` column; `pl processing promotion
  <stepId>` prints the source step, its last success, this step's last success, what Promote would
  do, and (when blocked) what unblocking would start.
- **API** — `GET /api/processing-steps/{stepId}/promotion` (one step) and
  `GET /api/pipelines/{pipelineId}/processing/promotions` (all steps) return `promote` (and
  `onUnblock` while blocked): `{ allowed, bundle, reason, message }`.

A build is identified by **when its production run was created** (queued) and the **bundle id**
(the first 8 hex digits are shown). That is all the service records about a bundle: it does not record the
commit a build came from, and the `VERSION` your build script stamps into `version.txt` is written
inside the job, so it is not known to the service either (it is usually a few seconds after the
build time shown).

The UI sends what it showed you along with the click — for Promote the bundle, for unblock the
bundle it said would start (or "none"). If that changed in between (say a newer build passed the
previous step), the server refuses (*"Refresh and try again"*) rather than start a build you
didn't see; a refused unblock leaves the step **blocked**. The CLI does the same with
`pl processing promote <stepId> --expect <bundleId>` and
`pl processing unblock <stepId> --expect <bundleId>|none` (API: `expectedArtifactBundleId` query
parameter on promote, `expectedPromotion` in the unblock body).

## The rules

| Situation | Promote | Unblocking a blocked step |
|---|---|---|
| Step is blocked | **refused** — the brake stays a brake | (it is being unblocked) |
| Step has no configuration | refused | nothing started |
| Nothing has ever succeeded at the previous step (no completed production run, for the first step) | refused | nothing started |
| The previous step's last success refers to a bundle whose build record is gone or never completed | refused — it never falls back to an older success | nothing started |
| A job for this step is already scheduled or running | refused (wait or cancel it) | nothing started |
| This step already ran exactly that bundle successfully | **runs it again** (retry / redeploy) | nothing started (nothing due) |
| That bundle is **older** than what this step last ran — its last success, or its latest run that started (a failed deploy may have half-applied) | refused — never goes backwards | nothing started — no automatic downgrade |
| Otherwise (newer, or the step never succeeded) | runs it | **starts the promotion** |

"Older" is judged by when each bundle's production run was created. Unblocking a step that was not
blocked changes nothing and runs no check. Every response (API `promotion` field, CLI output, UI
notice) says whether a promotion started and with which build, or why not. The `reason` codes are
`blocked`, `notConfigured`, `nothingPassedSource`, `bundleGone`, `jobActive`, `alreadyRan`,
`wouldDowngrade`, `unknownOrder`, plus `notBlocked` (unblock of a step that wasn't blocked) and
`unknownStep` (the step is not in its pipeline's step list).

> **Changed for existing callers:** `PUT /api/processing-steps/{stepId}/promotion` with
> `{ "blocked": false }` on a **blocked** step now also starts the promotion that is due. Older
> `pl` binaries and scripts that only read `blocked` will not print which build started — check
> the step's jobs, or update `pl`. `GET …/promotion` now returns the full read model (it still
> has `blocked`). `{ "blocked": true }` and unblocking a step that was not blocked behave as before.

Retrying a flaky deploy is just Promote: with nothing newer through the previous step, it is the
same bundle again.

## State, not config — and why that matters

The gate is **operational state**, deliberately kept *out* of GitOps config:

- It is **API/UI-mutable even on a git-bound pipeline.** Blocking a deploy is an operation, not
  a shape change, so the [git-only restriction](binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints)
  does not apply to it.
- It is stored **separately from the step** (keyed on the step id; absence = enabled), so a
  **reconcile never erases an operator's brake.** You can pause a deploy and keep pushing code;
  the brake survives every reconcile until you lift it.
- It is **persisted** independently, so a braked step stays braked across a server restart.

What Promote would use is derived from the **persisted job history** (each step's last
successful run, and each production run), so it is the same after a restart.

## Orthogonal to the job result

`blocked` is **orthogonal** to the step's job status. A step can be `Done` *and* have its
promotion blocked — the brake controls whether the **next** bundle advances in, not whether the
last run succeeded. Don't read a green step as "promotion is open," or a blocked gate as "the
step failed." They're independent axes.

## Typical uses

- **Freeze production deploys** during an incident: brake the prod processing step. Builds and
  beta deploys keep running; nothing reaches prod until you unblock (and unblocking then promotes
  the newest build that passed the step before prod — check which first).
- **Retry a flaky deploy** without rebuilding: Promote the step.
- **Stage a release**: brake prod, let beta validate, then **unblock** — the build that passed the
  step before prod is promoted into it. The confirmation (UI) or `pl processing promotion <stepId>`
  (CLI) shows which build that is before you unblock; if it changes before you confirm, the
  unblock is refused and the step stays blocked.
- **Rebuild the same commit**: the **Fresh build** button on the pipeline page (or
  `pl production trigger <pipelineId>`) starts a new production run of the branch head, even if
  the head has not moved. The new build flows through the steps as usual and stops at any brake.

Promoting a specific older build (a rollback) is not supported.

## CLI

| `pl` command | Description |
|---|---|
| `pl processing list <pipelineId>` | Steps in order, with gate and what Promote would use |
| `pl processing promotions <pipelineId>` | Gate, Promote and on-unblock for every step |
| `pl processing promotion <stepId>` | One step in detail |
| `pl processing block <stepId>` | Engage the brake |
| `pl processing unblock <stepId> [--expect <bundleId>\|none]` | Release it; prints the promotion it started, or why none |
| `pl processing promote <stepId> [--expect <bundleId>]` | Run the step with what passed the step before it |

`pl processing re-promote` and `POST /api/processing-steps/{stepId}/re-promote` were **removed**:
they re-ran a step with its *own* last bundle, which after a staged release redeployed the
previous version. Both remain for one release only to fail with a pointer to `promote`; neither
starts anything.

## How it relates to execution

Every path that would create a ProcessingJob consults the gate first — the automatic cascade
after a build, the manual processing trigger, configured `processing` triggers, Promote and the
unblock check. A blocked gate short-circuits them all, halting the chain at that step without
skipping ahead. The automatic cascade still passes each run's own bundle to the next step; the one
thing it now skips is scheduling a bundle that is already scheduled or running for that step
(which only an unblock, promote, manual `trigger/processing` or configured processing trigger of
that same bundle can cause). The gate check and job creation of every one of these paths runs
under one per-pipeline lock, so they are ordered strictly one after another. See
[Bundles & Execution](bundles-and-execution.md) for the job model and bundle retention.

## See also

- [Binding & Reconcile](binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints) — why
  the gate is exempt from the git-only restriction
- [Bundles & Execution](bundles-and-execution.md) — the sequential processing chain the gate
  controls
