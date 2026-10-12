# Bundles & Execution

[← Index](index.md) · [Subject Index](subjects.md)

How steps actually run, what an ArtifactBundle is, and how output flows from the parallel
production phase into the sequential processing phase.

## Every step is a Kubernetes Job

A step's `configuration` — `(image, script, environmentVariables)` — becomes a Kubernetes Job.
Each Job gets:

- The container **image**.
- The **script** to run.
- **Environment variables** from the step configuration.
- **Pipeline secrets** — the pipeline's k8s secret (`olve-pipeline-{id}`) is auto-mounted, so
  declared secrets are available as env vars (and via `$SECRET:NAME` in configured values).
- **Bundle references** — S3 keys for the input and/or output ArtifactBundle.

Secrets are injected directly from Kubernetes into the Job; they never pass through the app at
runtime.

## The two phases

```
Production [N steps, parallel] ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N [sequential]
```

### Production — parallel, produces the bundle

All production steps run **at the same time**. Each writes its output to `bundle/<step-name>/`.
When they all finish, the combined directory tree is zipped and stored in S3 as one
**ArtifactBundle**:

```
bundle/
  build/            <- output of the "build" production step
    image.tar
    ...
  package/          <- output of the "package" production step
    ...
```

### Processing — sequential, consumes the bundle

Processing steps run **one at a time, in list order**. Each receives the **full ArtifactBundle**
as input (every production step's output, combined). Because they're sequential, an earlier
processing step **gates** the later ones: if `deploy-beta` fails, `deploy` (prod) never runs.

This ordering is the mechanism behind the common "beta gates prod" pattern — see the
[worked example](config-reference.md#worked-example--this-repos-own-config).

## ArtifactBundle

The ArtifactBundle is the collected outputs as a **zipped directory in S3**:
`bundle/<step-name>/<files>`. It is *produced by* production and *consumed by* every processing
step. You can list and fetch bundles with the `pl` CLI:

| `pl` command | Description |
|---|---|
| `pl bundle list <pipelineId>` | List the pipeline's bundles |
| `pl bundle get <bundleId>` | Get one bundle |

(`pl bundle list` shows the bundles the running controller has seen since it last started; the
bundle records are in memory only. Promotion does not depend on them — it reads the persisted job
history.)

### Retention

A bundle's files (every step's output, plus the job logs) live under
`bundles/<pipeline>/<bundle>/` in the instance's storage. A daily sweep deletes a bundle only when
it is **both** older than 6 months (by its newest file) **and** not among its pipeline's 20 most
recent bundles (`Storage:Retention:{Enabled,KeepLatest,MinAgeDays,IntervalHours}`). On top of
that, a bundle that [Promote](promotion-gate.md) may still run is **never** deleted, whatever its
age: the latest completed production run, each processing step's last successful bundle, and the
bundle of any scheduled or running job.

## Jobs and scheduling

Jobs are first-class persisted entities managed by a **JobQueue** that controls when they're
submitted to Kubernetes. There are two job types:

- **ProductionJob** — runs all production steps, produces an ArtifactBundle.
- **ProcessingJob** — runs a single processing step with a given ArtifactBundle.

**Statuses:** `Scheduled`, `InProgress`, `Done`, `Obsolete`, `Cancelled`.

**Scheduling rules:**

- **Keyed on (pipeline, step)** — each step can have at most one `InProgress` job at a time.
- **Latest-wins** — scheduling a new job for a key that already has `Scheduled` jobs marks the
  older ones `Obsolete`. Only the newest scheduled job runs.
- **Cascade on pipeline delete** — deleting a pipeline cancels all its scheduled/in-progress
  jobs.

### Job commands

| `pl` command | Description |
|---|---|
| `pl job list [--pipeline <id>]` | List jobs (latest first; filter by pipeline) |
| `pl job get <jobId>` | Get a job |
| `pl job logs <jobId>` | Print a job's logs |
| `pl job queue` | Show the scheduled job queue |
| `pl job cancel <jobId>` | Cancel a job (operational — works on a bound pipeline) |

## How a build flows end to end

1. A trigger fires (push via the deploy poll, a manual production trigger, or a configured
   trigger) → a **ProductionJob** is scheduled.
2. Production steps run in parallel; their combined output is zipped into an **ArtifactBundle**
   in S3.
3. The bundle is **promoted** into the first processing step (subject to its
   [promotion gate](promotion-gate.md)) → a **ProcessingJob**. A blocked step holds the chain;
   unblocking it (or Promote) later runs it with the bundle that last succeeded at the step
   before it.
4. Each processing step runs in turn; a failure stops the chain. The bundle propagates through
   the pipeline automatically.

## See also

- [Promotion Gate](promotion-gate.md) — pause, release or retry the bundle into a processing step
- [`config.yaml` Reference](config-reference.md) — how to declare the steps that become these jobs
