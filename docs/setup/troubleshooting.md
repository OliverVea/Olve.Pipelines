# Troubleshooting

[← Index](index.md) · [Subject Index](subjects.md)

Symptoms → causes → fixes. Start with `pl binding status <pipelineId>` (reconcile) or
`pl run get <pipeline>#<run>` (a run).

## The reconcile shows `Error`

Nothing changed: no version was added and the active version keeps running. The first problem
usually says which phase failed. Reproduce locally with `pl dev describe` and `pl dev validate`.

| Problem mentions | Cause | Fix |
|---|---|---|
| `cannot fetch <repo>@<sha>` | The build Job couldn't download the tarball. Private repos need a token; a token key that isn't set means no token. | `pl secret set <pipelineId> GITHUB_TOKEN`, then `pl binding set-credentials <pipelineId> GITHUB_TOKEN` |
| `bun install` / lockfile errors | `bun.lock` missing or out of date, or `@olve/pipelines` can't be resolved | Run `bun install` in the pipeline directory and commit `bun.lock`. The SDK resolves only from an in-repo `sdk/` today (see [Getting Started](getting-started.md#before-you-start-the-sdk-is-not-published-yet)). |
| exit `78`, describe problems | The pipeline is invalid: missing handler file, duplicate step name, bad mapper, two schemas for one model name, … | `pl dev describe` prints the same problems |
| a manifest check (ambiguous producer, unavailable ref, unknown `when` field, undeclared secret, unknown mapper key, …) | A [reconcile check](pipeline-reference.md#checks-that-reject-a-pipeline) failed | `pl dev validate` runs the same checks |
| `PT_INTERP` | The binary was built for musl | Never build on an alpine Bun image (the service uses `oven/bun:*-slim`) |

A failed reconcile is **not retried** by the background poll. After fixing, push, or run
`pl binding reconcile <pipelineId>`. Warnings (e.g. an output nothing consumes) don't fail a
reconcile; `pl binding status --json` lists them.

## I pushed but no run started

- **Wait for the poll.** It runs every 15 minutes (webhook modes react within seconds, but v2 beta
  has no public webhook). Force it: `pl binding reconcile <pipelineId>`.
- **No `onPush`.** A pipeline without `.onPush(…)` ignores pushes.
- **The first observation only records the head.** Right after binding, start the first run with
  `pl trigger`.
- **De-duplicated.** The same mapped input already has a running run, or the most recent automatic
  run had the same input less than 1 hour ago. Use `pl trigger` or `pl run rerun` — explicit actions
  are never de-duplicated.
- **The config commit failed to reconcile.** While the newest commit under the binding path is
  unapplied, runs are held. Check `pl binding status`.
- **Queued behind a switch.** While the pipeline drains, new runs wait and start on the new version.
  `pl pipeline health <pipeline>` shows the activation.

## My change didn't take effect

- **Only commits under the binding path build a new binary.** A change outside it — for example to
  an in-repo `sdk/` — reaches the binary with the next commit that touches the binding path.
  `pl binding reconcile` doesn't help: it rebuilds the same config commit.
- **The run pinned an older version.** A run always uses the version it started on.
  `pl run get` shows it; `pl pipeline versions` shows which one is active.
- **Someone switched back.** A manual `pl pipeline switch` holds until the next config commit.

## A run is stuck

- **Held at a gate.** `pl run get` shows `held` on the step. `pl gate list <pipeline>`, then
  `pl gate unblock <pipeline>/<step>` — see [Promotion Gates](promotion-gate.md).
- **Waiting for another run.** Only one attempt per step name is in progress at a time; a newer
  run's attempt waits for the older one (including a superseded run's deploy finishing).
- **A step hangs.** A hung step keeps its slot until its `timeoutSeconds` (plus grace) expires,
  and the next push's attempt of that step waits behind it. Kaniko builds have been seen to hang at
  "Unpacking rootfs" (a stalled base-image pull). Recover with
  `pl run cancel <pipeline>#<n>` then `pl run rerun <pipeline>#<n>`.

## A run is `Superseded`

A newer automatic run (push or poll) superseded it. Its in-progress attempts finished, but it never
advanced. Follow `supersededBy` in `pl run get`. Manual runs never supersede anything.

## A step failed

`pl step logs <pipeline>#<run>/<step>` shows the log; the attempt's reason includes the harness exit
code:

| Exit | Meaning | Look at |
|---|---|---|
| 1 | The handler threw | the log |
| 65 | `input.json` invalid for the step | the mapping / the upstream output |
| 66 | The output failed strict validation (unknown or missing keys), or an own file ref changed | the handler's return value |
| 74 | Uploading the result failed after retries | storage reachability from the runner namespace |
| 78 | The pipeline is invalid / handler module missing | rebuild; should have failed at reconcile |
| 130 / 143 | Cancelled, or the step hit its deadline | `timeoutSeconds` |

"No shell" errors mean the handler used `ctx.sh` on a shell-less image — use `ctx.exec`. Reproduce
in the real image with `pl dev run <step> --docker`.

## Continue or rerun is refused

- `cannot change … consumed`: `pl run continue` can only change input fields no completed step
  consumed (e.g. not `sha`).
- The run's version is no longer active (the pipeline switched): continue and step rerun need the
  active version. Re-trigger instead.
- `pl step rerun` needs the run to be finished, or the step's latest attempt to have failed or been
  cancelled. A blocked step can't be rerun until it is unblocked.

## Trigger input is rejected

Every problem is listed and nothing starts. `--field` values are typed by the input model of the
active version; required fields without defaults must be given (e.g. a 40-character `sha` — use
`--field sha=$(git rev-parse HEAD)`).

## A secret shows `unset` or `unknown`

- `unset`: declared but not set — `pl secret set <pipelineId> <NAME>` (reads stdin; don't use
  `$(…)`, it strips trailing newlines from keys).
- `unknown`: Kubernetes couldn't be read at that moment. It doesn't mean missing.

A step only receives the secrets it declares. A secret used by a step but missing from its
`secrets: [...]` is a compile and reconcile error.

## A failure handler didn't run

Failure handlers are best-effort and untracked: they don't appear in the API or UI. Their input and
log are in storage under `runs/<pipelineId>/<run>/_failure/`. A handler only runs for failures of
steps in its scope (`onFailure(handler, { steps })`), and is skipped (with a log line in the
service) when its `FailureContext` model is from an older SDK than the service's.

## Downloading a file returns 401

Reads are anonymous, except listing and downloading step files, which need a session (`pl login`).

## See also

- [Binding, Reconcile & Versions](binding-and-reconcile.md)
- [Runs & Operations](runs-and-operations.md)
- [Local Development](local-development.md)
