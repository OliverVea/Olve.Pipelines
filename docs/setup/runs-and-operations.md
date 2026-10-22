# Runs & Operations

[← Index](index.md) · [Subject Index](subjects.md)

What happens after a trigger: runs, attempts, scheduling, and how to inspect and steer them with
`pl` or the UI.

## Runs and attempts

A **run** is one execution of one pinned pipeline version. It stores its input and, through its
attempts, every step's output (the **pool**). Runs are numbered per pipeline — the build number.

| Run field | |
|---|---|
| number | monotonic per pipeline: `my-app#12` |
| version | the pipeline version it pinned at start (never changes) |
| version stamp | `yyyy.MM.dd.<number>` (UTC creation date), e.g. `2026.10.22.6`; `ctx.run.version`, the `imageBuild` tag |
| trigger | `Push`, `Poll` or `Manual` |
| status | `Running`, `Succeeded`, `Failed`, `Cancelled`, `Superseded` |
| links | `supersededBy`, `rerunOf` |

An **attempt** is one execution of one step in a run, run as **one Kubernetes Job**. Reruns add
attempts (`@1`, `@2`, …). Attempt statuses: `Scheduled`, `InProgress`, `Succeeded`, `Failed`,
`Skipped` (with a reason), `Cancelled`, `Obsolete`. Later stages consume a step's **latest
successful attempt**.

A failed step halts the run at its stage barrier: the run is `Failed` and the next stage doesn't
start — unless the step opted in to `retries: n`, in which case a failed attempt is retried
automatically (a new attempt in a fresh pod) until it has failed 1 + n times in a row.

## Scheduling rules

- **One in progress per step.** Across all runs of a pipeline, at most one attempt per step name is
  `InProgress`. A newer run's attempt of the same step waits for it.
- **Latest-wins.** Among `Scheduled` attempts of one step, only the newest runs; older ones become
  `Obsolete`.
- **Supersede.** A new *automatic* run (push or poll) supersedes every older running run of the
  pipeline: the older run becomes `Superseded` (linked to the new one), its scheduled attempts are
  cancelled, and its in-progress attempts finish (a half-done deploy is never killed) but the run
  never advances.
- **De-duplication (automatic triggers only).** The key is the mapped input. A push and a poll for
  the same commit make one run. An automatic trigger is dropped when a running run on the same
  version has the same input, or when the most recent automatic run has the same input, wasn't
  cancelled and started less than **1 hour** ago. (Comparing with the latest run only means
  A → B → A deploys A again.)
- **Explicit actions always create work.** `pl trigger`, the UI form, reruns and continues are never
  de-duplicated and never supersede anything.
- **Gates.** A blocked step holds a run that reaches it — see [Promotion Gates](promotion-gate.md).
- **Switching versions.** While a pipeline drains, new runs are queued and start on the new
  version — see [Switch and rollback](binding-and-reconcile.md#switch-and-rollback).

## Addresses

```
<pipeline>#<run>                     a run                     my-app#12
<pipeline>#<run>/<step>              latest successful attempt my-app#12/deploy-beta
<pipeline>#<run>/<step>@<attempt>    a specific attempt        my-app#12/deploy-beta@1
<pipeline>#<run>/<step>/<path>       a file of that attempt    my-app#12/build/image.tar
<pipeline>/<step>                    a step across runs        my-app/deploy-beta
```

`<pipeline>` is the pipeline id or name (quote names with spaces). Without `@attempt`, `pl step logs`
shows the latest attempt whatever its status; output and files use the latest successful one.

## Operating runs

| Do this | `pl` | UI |
|---|---|---|
| Start a run | `pl trigger <pipeline> [--input json\|@file] [--field k=v …]` — prints `<pipeline>#<run>` | Pipeline view → trigger form (run plan preview) |
| List runs | `pl run list <pipeline>` | Pipeline view → recent runs |
| Inspect a run | `pl run get <pipeline>#<run>` — input, version, stages, each step's status/reason/gate | Run view |
| Cancel | `pl run cancel <pipeline>#<run>` | Run view → Cancel |
| Rerun the whole run | `pl run rerun <pipeline>#<run>` | Run view → Rerun |
| Continue with changed input | `pl run continue <pipeline>#<run> --field deployTo=prod` | Run view → Continue |
| Rerun one step | `pl step rerun <pipeline>#<run>/<step> [--continue]` | Run view → step → Rerun |
| Output, files, logs | `pl step output\|files\|file\|logs <address>` (`logs --follow`) | Run view → step |
| A step's history | `pl step history <pipeline>/<step>` | `/pipeline/<id>/step/<name>` |
| Health | `pl pipeline health <pipeline>`, `pl pipeline list` | Pipeline list |

- **Trigger.** The body is the pipeline input. `--field` values are typed by the active version's
  input model (`sha=0123` stays a string); the model's defaults fill the rest. Invalid input is
  rejected with every problem listed and nothing starts. The response says whether the run
  `started` or was `queued` behind a version switch.
- **Cancel** cancels the run and its scheduled *and in-progress* attempts (their Jobs are deleted) —
  unlike supersede. On a run that already finished but still has active attempts (e.g. a superseded
  run's deploy), only those attempts are cancelled.
- **Rerun run** creates a new run with the same input on the version a run started now would use,
  linked via `rerunOf`. The input is re-validated against that version.
- **Continue** keeps the completed stages and re-evaluates `when` for the rest. Changed fields are
  merged over the stored input and validated against the run's pinned input model. A field a
  completed step already consumed (e.g. `sha`) can't change — this is "promote what you tested".
- **Rerun step** adds an attempt with the same pinned version and current pool input. Allowed when
  the run has finished, or when that step's latest attempt failed or was cancelled. *Rerun only*
  leaves the rest of the run alone; `--continue` re-opens the run, and later stages run again where
  their input changed.
- **Cancel step** (`pl step cancel <pipeline>#<run>/<step>`) cancels that step's scheduled or
  in-progress attempt (its Job is deleted). A cancelled attempt is treated like any other: the other
  steps of its stage keep running, and once the stage settles the run ends Cancelled. To carry on,
  `pl step rerun <address> --continue`.
- **Continue and rerun need the run's version to be active.** After a switch, re-trigger instead.

Health (`pl pipeline list`):

| Status | Meaning |
|---|---|
| `Healthy` | nothing running; the latest settled run succeeded |
| `Running (Healthy)` | a run is running; the latest settled run did not fail |
| `Running (Unhealthy)` | a run is running; the latest settled run failed |
| `Unhealthy` | nothing running; the latest settled run failed |
| `Idle` | no settled run and nothing running |

Cancelled and superseded runs are ignored; skipped steps never count as unhealthy.

What's running across all pipelines: `pl attempts` (`GET /api/attempts?status=active`) lists every
scheduled or in-progress attempt by address (`<pipeline>#<run>/<step>@<attempt>`).

## Live events

`GET /api/events[?pipeline=<id>]` is a server-sent event stream (anonymous, like other reads) of
small change notices: `run`, `attempt`, `version`, `gate`, `binding`, `pipeline`, each with the
pipeline id and just enough to know what to reload (shapes in the repo README, "Live events").
A UI or script re-fetches on each notice instead of polling:

```bash
curl -N "https://<instance>/api/events?pipeline=<id>"
# event: attempt
# data: {"pipelineId":"…","run":12,"step":"build","attempt":1,"status":"in-progress"}
# id: 1761131820000-42
```

Every connection starts with `hello`; a reconnect whose `Last-Event-ID` is stale, or from before a
restart, gets `resync` (refetch everything, nothing is replayed). `ping` arrives every 15 s while
idle. Open the stream before loading the page's data, so nothing falls between the two.
`pl watch [<pipeline>]` prints the events.

A running attempt's log streams from `GET …/runs/<n>/steps/<step>/logs/stream[?attempt=]`: `log`
events carry new text (`reset: true` means replace what you have, e.g. when the step container
takes over from the init container), and `end` carries the final status. The server re-reads the
pod log every 2 s, so this is "live" to within a couple of seconds.

## How a step runs

Each attempt is a Kubernetes Job in the instance's runner namespace, under the runner hardening
(gVisor runtime class, Pod Security baseline, NetworkPolicy):

1. **Init container** (`harness-base`, a 2.4 MB static busybox image with a bundled glibc loader)
   downloads the version's compiled binary (`step.tar.gz`) and the attempt's `input.json` through
   pre-signed URLs into a shared `/olve` volume.
2. **Step container** — the step's declared image — runs
   `/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step run <step>`. That works on
   debian, Alpine (musl), kaniko and distroless images; child processes use the image's own libc and
   tools. `TMPDIR=/olve/tmp`.
3. The harness validates the input, calls the handler, validates the output strictly, and uploads
   `output.json` + output files as `result.tar.gz` through a pre-signed PUT.
4. The service unpacks it into storage under `runs/<pipelineId>/<run>/<step>/<attempt>/` and keeps
   the pod log there too.

The step gets env vars `OLVE_RUN`, `OLVE_ATTEMPT`, `OLVE_RUN_ID`, `OLVE_PIPELINE`,
`OLVE_VERSION_STAMP`, its declared secrets (same names), and pre-signed URLs for the files in its
input. Its deadline (`activeDeadlineSeconds`) is the step's `timeoutSeconds` (default 3600, max
21600) plus a startup grace. Repo code never runs in the service. If the service restarts — for
example because a step just redeployed it — it reattaches to running Jobs by name and collects them.

Harness exit codes in a failed attempt's reason: `1` handler threw, `65` input invalid, `66` output
invalid, `74` result upload failed, `78` pipeline invalid, `130`/`143` cancelled or deadline. Full
contract: [sdk/README.md "Harness contract"](https://github.com/OliverVea/Olve.Pipelines/blob/v2/sdk/README.md#harness-contract-service--step-binary).

## Storage

Each instance has its own Garage (S3-compatible) bucket:

```
versions/<pipelineId>/builds/<sha>/step.tar.gz      compiled binary per config commit
versions/<pipelineId>/<n>/manifest.json             one manifest per version
runs/<pipelineId>/<run>/run.json                    the run and all its attempts
runs/<pipelineId>/<run>/<step>/<attempt>/output.json
runs/<pipelineId>/<run>/<step>/<attempt>/files/…    a directory ref dir/ is stored as dir.tar
runs/<pipelineId>/<run>/<step>/<attempt>/log
runs/<pipelineId>/<run>/_failure/<handler>/<id>/    failure-handler input + log
```

A file reference carries its full address (`{ run, step, attempt, path, sha256 }`), so continues and
reruns reuse earlier outputs without copying them.

## Retention

A run is deleted only when it is **both** older than 183 days **and** not among its pipeline's 20
newest runs; a running run never is. Its outputs, files, logs and failure-handler objects go with
it. A compiled binary is kept while the active/draining version or any retained run uses it, and
deleted 7 days after that. Leftovers (objects of deleted runs, stale result archives, URL Secrets,
leaked step Jobs) are swept too. Step Jobs have a 7-day TTL.

Retention runs 2 minutes after start-up and then daily, and is a **dry run by default**: it logs
what it would delete. An operator turns it on with `Retention__DryRun=false`; every threshold is a
`Retention:*` setting (see the repo README).

## See also

- [Promotion Gates](promotion-gate.md) — hold runs at a step
- [Binding, Reconcile & Versions](binding-and-reconcile.md) — where versions come from
- [Troubleshooting](troubleshooting.md)
