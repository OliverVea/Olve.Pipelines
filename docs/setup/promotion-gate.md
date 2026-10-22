# Promotion Gates

[← Index](index.md) · [Subject Index](subjects.md)

A **gate** is a brake on one step of a pipeline. While a step is blocked, a run that reaches it is
**held** there: the service creates no attempt for the step, and the run waits. Unblocking lets the
held runs resume. Gates are how you pause deploys without touching the pipeline definition.

```sh
pl gate block my-app/deploy-prod       # runs reaching deploy-prod are held there
pl gate list my-app                    # the pipeline's blocked steps
pl gate unblock my-app/deploy-prod     # held runs resume
```

In the UI, each step in the pipeline view has a block/unblock toggle, and a held step shows as
`held` in the run view (`pl run get` shows `held` / `blocked` per step).

## Rules

- **Keyed on the step name**, not on a version. A blocked step stays blocked across version
  switches and restarts (persisted in `pipeline-state.json`).
- **Operational state, not config.** Gates are not in `pipeline.ts`; reconcile never touches them, and
  they can be changed on a bound pipeline.
- Blocking affects only **new** attempts. An attempt already running finishes.
- The run is held at the stage containing the blocked step. Other steps of that stage still run;
  the stage barrier keeps later stages waiting.
- `blocked` is independent of results: a step can have succeeded last time *and* be blocked now.
- Every run that reaches the step is held, however it started: push, poll, manual trigger,
  continue or rerun-and-continue. An explicit `pl step rerun` of the blocked step itself is refused.

## Held runs and newer runs

- A newer automatic run supersedes a held one, as with any running run. So while a gate is closed,
  pushes keep building up to the gate, and unblocking resumes only the newest run.
- A held run doesn't block a version switch. When the switch completes, runs left on the old version
  — held ones included — are cancelled; re-trigger on the new version.

## Promoting what you tested

v1's "promote" action is replaced by continuing a run:

- `pl run continue my-app#12 --field deployTo=prod` keeps the stages that already ran and
  re-evaluates `when` for the rest — the same commit, the same build. Fields that a completed step
  already consumed (e.g. `sha`) can't change.
- `pl step rerun my-app#12/deploy-beta --continue` reruns one step and then continues downstream.

## Typical uses

- **Pause deploys** while you work on the target environment: block the deploy step; builds and
  tests keep running on every push and wait at the gate. Unblock to deploy the newest one.
- **Manual approval for prod**: keep `deploy-prod` blocked; unblock when you want a held run to go
  out, then block again.
- **Stop a self-deploy**: Olve.Pipelines' own v2 deploy can be paused with
  `pl gate block "Olve.Pipelines v2/deploy-beta"` against v2 prod; its `deploy-prod` gate holds
  promotions.

## API

| Method | Path | |
|---|---|---|
| GET | `/api/pipelines/{pipelineId}/gates` | blocked step names |
| POST | `/api/pipelines/{pipelineId}/steps/{step}/block` | block (returns the blocked set) |
| POST | `/api/pipelines/{pipelineId}/steps/{step}/unblock` | unblock; held runs resume |

## See also

- [Runs & Operations](runs-and-operations.md) — continue, rerun, supersede
- [Binding, Reconcile & Versions](binding-and-reconcile.md) — switches
