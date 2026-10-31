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
| `bun install` / lockfile errors | `bun.lock` missing or out of date, or `@olivervea/pipelines` can't be resolved | Run `bun install` in the pipeline directory and commit `bun.lock`. Depend on `"@olivervea/pipelines": "<version>"` (see [Getting Started](getting-started.md#the-sdk)). |
| exit `78`, describe problems | The pipeline is invalid: a step without an implementation, duplicate step name, bad mapper, two schemas for one model name, … | `pl dev describe` prints the same problems |
| a manifest check (ambiguous producer, unavailable ref, unknown `when` field, undeclared secret, unknown mapper key, …) | A [reconcile check](pipeline-reference.md#checks-that-reject-a-pipeline) failed | `pl dev validate` runs the same checks |
| `PT_INTERP` | The binary was built for musl | Never build on an alpine Bun image (the service uses `oven/bun:*-slim`) |

A failed reconcile is **not retried** by the background poll. After fixing, push, or run
`pl binding reconcile <pipelineId>`. Warnings (e.g. an output nothing consumes) don't fail a
reconcile; `pl binding status --json` lists them.

## I pushed but no run started

- **Wait for the poll.** It runs every 15 minutes (a webhook, where the instance has a public URL, reacts within seconds;
  neither v2 instance has one). Force it: `pl binding reconcile <pipelineId>`.
- **No `onPush`.** A pipeline without `.onPush(…)` ignores pushes.
- **The first observation only records the head.** Right after binding, start the first run with
  `pl trigger`.
- **De-duplicated.** The same mapped input already has a running run, or the most recent automatic
  run had the same input less than 1 hour ago. Use `pl trigger` or `pl run rerun` — explicit actions
  are never de-duplicated.
- **The config commit failed to reconcile.** While the newest commit under the binding path is
  unapplied, runs are held. Check `pl binding status`.
- **The new version is pending.** Its config isn't satisfied (a missing or invalid entry, or a
  referenced secret that isn't set). `pl binding status` shows `Pending: …` with the reasons; fix
  them with `pl config set` or `pl secret set` and the version activates and the push runs.
- **Queued behind a `--cancel-running` switch.** For the moment it takes, new runs wait and start on
  the new version. `pl pipeline health <pipeline>` shows the activation.

## A version stays pending, or new runs are refused as held

- **Pending:** the config document doesn't satisfy the version. `pl config check <pipeline>` lists
  the problems and the missing secrets; save a fixed document (`pl config set`) or set the secret.
- **Held:** a secret the active version's config references was deleted. Set it again
  (`pl secret set`); new runs are refused until then.
- **`pl config set` is refused:** the document is invalid for the *active* version. Fix the listed
  fields; keys the active version doesn't declare are fine.

## My change didn't take effect

- **Only commits that touch the binding path or its local package dependencies** (`file:` / `link:`
  entries in its `package.json`) build a new binary. A change anywhere else — app code, the chart —
  is picked up by the *run* (it builds from the pushed commit), not by the pipeline definition.
- **A run uses the config it started with.** Continue and step rerun keep the run's config
  snapshot (`pl run get` shows `config #N`); start a new run to use newer config.
- **The run pinned an older version.** A run always uses the version it started on.
  `pl run get` shows it; `pl pipeline versions` shows which one is active.
- **Someone switched back.** A manual `pl pipeline switch` holds until the next config commit.

## A run is stuck

- **Held at a blocked step.** `pl run get` shows `held` on the step. `pl step blocked <pipeline>`,
  then `pl step unblock <pipeline>/<step>` — see [Blocking Steps](blocking-steps.md).
- **Waiting for another run.** Only one attempt per step name is in progress at a time; a newer
  run's attempt waits for the older one (an older run's deploy finishing, say).
- **A step hangs.** A hung step keeps its slot until its `timeoutSeconds` (plus grace) expires,
  and the next push's attempt of that step waits behind it. Give long-running commands an
  `idleTimeoutSeconds` (`ctx.exec`/`ctx.sh`) and the step `retries`. Kaniko can hang at "Unpacking
  rootfs" (a stalled base-image pull); `steps.imageBuild`'s stall watchdog fails it after 300 s of
  silence. The stall is kaniko under gVisor pulling from mcr.microsoft.com; a pull-through cache in
  `imageBuild`'s `registryMap` avoids it (a log line says when a cache was unreachable and skipped). To recover by hand: `pl run cancel <pipeline>#<n>` then `pl run rerun <pipeline>#<n>`, or
  `pl step rerun <pipeline>#<n>/<step> --continue` for just the failed step.

## A run is `Superseded`

Its attempt of some step was waiting at the same time as a newer run's attempt of that step, and
the newest wins. The newer run carries on from there; follow `supersededBy` in `pl run get`
(`pl wait` follows it for you).

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

## A request returns 401

Everything except the docs, `/download` and the install scripts needs a session: `pl login` (or the
web UI's sign-in).

## See also

- [Binding, Reconcile & Versions](binding-and-reconcile.md)
- [Runs & Operations](runs-and-operations.md)
- [Local Development](local-development.md)
