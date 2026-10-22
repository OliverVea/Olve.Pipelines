# Blocking Steps

[← Index](index.md) · [Subject Index](subjects.md)

**Blocking** a step is an operator hold on a pipeline: while a step is blocked, every run that
reaches it is **held** there. The service creates no attempt for the step and the run waits until
the step is unblocked.

Use it **sparingly**, for the times you need a pipeline to stop short of a step without changing
its definition: while you work on the pipeline itself or on the environment a step deploys to, or
during an incident. It is not an approval mechanism. A decision that a run may go on (tests, a
visual-regression check, an approval) belongs in the pipeline as a step or stage of its own; see
[Stages](pipeline-reference.md#stages).

```sh
pl step block my-app/deploy-prod       # runs reaching deploy-prod are held there
pl step blocked my-app                 # the pipeline's blocked steps
pl step unblock my-app/deploy-prod     # held runs resume
```

In the UI, each step in the pipeline view has a lock toggle, and the step page has Block/Unblock.
A held step shows as `held` in the run view (`pl run get` shows `held` / `blocked` per step).

## Rules

- **Keyed on the step name**, not on a version. A blocked step stays blocked across version
  switches and restarts.
- **Operational state, not config.** Blocks are not in `pipeline.ts`; reconcile never touches
  them, and they can be changed on a bound pipeline.
- Blocking affects only **new** attempts. An attempt already running finishes.
- The run is held at the stage containing the blocked step. Other steps of that stage still run;
  the stage barrier keeps later stages waiting.
- `blocked` is independent of results: a step can have succeeded last time *and* be blocked now.
- Every run that reaches the step is held, however it started: push, poll, webhook, manual trigger,
  continue or rerun-and-continue. An explicit `pl step rerun` of the blocked step itself is refused.
- Unblock when you're done. A forgotten block silently holds every later deploy; `pl pipeline
  list` shows blocked steps per pipeline.

## Held runs and newer runs

- A newer automatic run supersedes a held one, as with any running run. So while a step is
  blocked, pushes keep running up to it, and unblocking resumes only the newest run.
- A held run doesn't block a version switch. When the switch completes, runs left on the old
  version, held ones included, are cancelled; re-trigger on the new version.

## Example

Working on the target environment of `my-app`'s beta deploy:

```sh
pl step block my-app/deploy-beta       # builds and tests keep running on every push
# … work on the beta environment …
pl step unblock my-app/deploy-beta     # the newest held run deploys
```

## API

| Method | Path | |
|---|---|---|
| GET | `/api/pipelines/{pipelineId}/blocked-steps` | blocked step names |
| POST | `/api/pipelines/{pipelineId}/steps/{step}/block` | block (returns the blocked set) |
| POST | `/api/pipelines/{pipelineId}/steps/{step}/unblock` | unblock; held runs resume |

The `blocked` event on `/api/events` carries the pipeline's full blocked list on every change.

## See also

- [Runs & Operations](runs-and-operations.md) — continue, rerun, supersede
- [Binding, Reconcile & Versions](binding-and-reconcile.md) — switches
