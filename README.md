# Olve.Pipelines

A lightweight CD pipeline configuration and orchestration service for a homelab. Manages pipeline definitions with production steps (parallel builds) and processing steps (sequential deployments), executed as Kubernetes Jobs.

## Pipeline Model

```
Production [N steps, parallel] ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N [sequential]
```

- **Production steps** run in parallel. Each produces output to `bundle/<step-name>/`. Combined output = ArtifactBundle.
- **Processing steps** run sequentially. Each receives the full ArtifactBundle.
- Every step is configured as `(image, script, env)` and executed as a Kubernetes Job.

### Promotion gate (state, not config)

A **promotion** is the ArtifactBundle advancing *into* a processing step. Each processing step
has a gate that can **block** promotion (the bundle stops at that step instead of cascading on) and
a **promote** action that runs the step with the bundle that **last succeeded at the step before
it** (for the first step, the latest completed production run). **Unblocking** a blocked step
starts that promotion if it is due (never an older build than the step last ran). The API, the
`pl` CLI and the UI all show which build Promote or unblock would use before you act — see
[docs/setup/promotion-gate.md](docs/setup/promotion-gate.md) for the full rules. This is
operational **state, not GitOps config**: it is API-mutable and stays available even on a
git-bound pipeline (configuration is git-only, but operations like blocking/unblocking and
promoting are allowed). The blocked set is persisted separately (`promotion-state.json`) so a
braked step stays braked across a restart. `blocked` is orthogonal to job status — a step can be
`Done` *and* have its promotion blocked. (`re-promote`, which re-ran a step with its own last
bundle, was removed. Note for existing API callers: `PUT …/promotion` with `{ "blocked": false }`
on a blocked step now also starts the due promotion.)

## GitOps Configuration

> **Adding CD to your own repo?** This is the section for you. You don't deploy or modify this
> service — you add one file to *your* repository and bind a pipeline to it.
>
> A fuller, agent-friendly **setup guide is served by the running instance at
> [`/docs`](docs/setup/index.md)** (raw Markdown, with a [subject index](docs/setup/subjects.md)).
> This README is the in-code reference; `/docs` is the operator/setup view.

A pipeline is **always bound to a Git repository** — that binding is the only way to create one.
*Your* repo is the single source of truth for the pipeline's shape: a background reconcile loop
polls your branch head (~5 min), and whenever `<your-repo>/.pipelines/config.yaml` changes it
materializes the production steps, processing steps, and triggers to match — then runs the build.
Binding also configures the deploy poll, so pushing a commit deploys automatically; you never
author a trigger by hand.

### Adding CD to your repo

1. Add `.pipelines/config.yaml` (+ optional `.pipelines/scripts/*.sh`) to your repository — see
   the schema below. [`Olve.Template.Api`](https://github.com/OliverVea/Olve.Template.Api) ships a
   copy-me [`.pipelines/`](https://github.com/OliverVea/Olve.Template.Api/tree/main/.pipelines)
   starter (Kaniko build + Helm deploy).
2. Create a pipeline bound to your repo: `POST /api/pipelines/with-repo` with
   `{ repo, branch?, path?, credentialsSecret }` (see [GitOps Binding](#gitops-binding)).
   This is the **only** way to create a pipeline — there is no unbound/draft create. The
   pipeline's name comes from `config.yaml`'s `name` (the bind seeds a provisional name from
   the repo until the first reconcile).
3. Set the secret *values* your config declares: `PUT /api/pipelines/{id}/secrets/{name}`.
4. Push. The first reconcile builds your pipeline from the file; check
   `GET /api/pipelines/{id}/binding/status` for the result.

**Git-only:** the API has **no config-mutation endpoints** — there is no way to create, delete,
reconfigure, or reorder steps or to create/delete triggers over HTTP. Pipeline shape comes solely
from your repo via reconcile. Operational endpoints stay open: manual production trigger, job
cancel, setting secret *values*, and the promotion gate. The dividing line: **shape is
git-owned; operations are API-allowed.**

### `config.yaml` schema

`<path>` defaults to `.pipelines`. Steps may be inlined or extracted: `$ref: steps/<name>.yaml`
pulls a step from its own file, and `scriptFile: scripts/<name>.sh` keeps a script out-of-line
(mutually exclusive with `script:`). Secrets are declared by **name only** — values live in the
pipeline's own k8s secret (`olve-pipeline-{id}`), never in the repo — and are referenced as
`$SECRET:NAME` (in env values / poll headers) or as ordinary env vars mounted into every job.

```yaml
apiVersion: "0.0"                 # major must match the server (currently 0.x)
name: my-app
description: Build and deploy.    # optional
version: "1"                      # optional, free-form

secrets:                          # declared by NAME ONLY (values never in repo)
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball.

productionSteps:                  # run in parallel → bundle/<step-name>/
  - name: build
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh        # or inline: script: |

processingSteps:                  # run sequentially (order = list position)
  - name: deploy
    configuration:
      image: alpine:latest
      script: |
        echo deploying...

triggers:                         # OPTIONAL & additive (the deploy poll is implicit)
  - name: redeploy
    target: { type: processing, processingStepName: deploy }
```

Validation rejects the whole reconcile on: incompatible `apiVersion`, duplicate step names, a
trigger referencing an unknown processing step, both `script` and `scriptFile` set, or a
`$SECRET:NAME` that isn't declared in `secrets:`. A broken or unfetchable config **holds off the
build** for that cycle (config-before-build), so bad config never ships on stale code.

This repo dogfoods the feature: see [`.pipelines/config.yaml`](.pipelines/config.yaml). To
bootstrap or recreate the bound pipeline, use the `setup-pipeline` skill.

## Project Structure

```
src/Olve.Pipelines/                             # API application (minimal API, AOT)
├── Configuration/                               # Auth, telemetry, JSON, host, storage config
├── Health/                                      # Health check endpoint
├── Jobs/                                        # Job scheduling, obsoletion, cancellation
├── Kubernetes/                                  # K8s client, secrets, job specs
├── Pipelines/                                   # Pipeline CRUD, events
│   ├── Building/                                # ArtifactBundle entity and endpoints
│   ├── Polling/                                 # Poll trigger background service
│   ├── Processing/                              # ProcessingStep entity, service, endpoints
│   └── Production/                              # ProductionStep entity, service, endpoints
├── Shared/                                      # EntityStore, AttachmentStore, events, persistence
└── Dockerfile                                   # Multi-stage build (AOT, chiseled)
test/Olve.Pipelines.UnitTests/                   # Unit tests (TUnit + Rocks)
test/Olve.Pipelines.IntegrationTests/            # Integration tests (TUnit + Testcontainers)
frontend/                                        # Dashboard UI (Lit + Vite + TypeScript)
clients/Olve.Pipelines.Client/                   # Generated C# client (Refitter source gen)
clients/olve-pipelines-client-ts/                # Generated TypeScript client (Kiota)
helm/                                            # Helm chart for Kubernetes
tools/version.cs                                 # CalVer versioning script
```

## Endpoints

### Pipelines

Pipelines are created only via `POST /api/pipelines/with-repo` (see [GitOps Binding](#gitops-binding)) — there is no unbound create.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines` | List pipelines |
| GET | `/api/pipelines/summary` | List pipelines with per-step health (for the list page; one round-trip) |
| GET | `/api/pipelines/{id}` | Get pipeline |
| DELETE | `/api/pipelines/{id}` | Delete pipeline (cascades) |
| POST | `/api/pipelines/{id}/trigger/production` | Trigger production jobs |
| GET | `/api/pipelines/{id}/processing/promotions` | List per-step promotion state: gate, and what Promote / unblock would use |

### Production Steps

Steps are defined in `.pipelines/config.yaml` and materialized by reconcile — they are not API-writable (read-only over HTTP).

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/production` | List production steps |
| GET | `/api/production-steps/{stepId}` | Get production step |
| GET | `/api/production-steps/{stepId}/configuration` | Get step configuration |

### Processing Steps

Steps and their order are defined in `.pipelines/config.yaml` and materialized by reconcile — not API-writable. The promotion gate is operational state and stays API-mutable.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/processing` | List processing steps (ordered) |
| GET | `/api/processing-steps/{stepId}` | Get processing step |
| GET | `/api/processing-steps/{stepId}/configuration` | Get step configuration |
| GET | `/api/processing-steps/{stepId}/promotion` | Get promotion gate and what Promote / unblock would use (`{ blocked, source, sourceLastSuccess, lastSuccess, promote, onUnblock }`) |
| PUT | `/api/processing-steps/{stepId}/promotion` | Block/unblock promotion (operational). Unblocking a blocked step starts the due promotion; `promotion` in the response says whether and with which bundle. Optional `expectedPromotion` (bundle id or `"none"`): refused, still blocked, if unblocking would start something else |
| POST | `/api/processing-steps/{stepId}/promote` | Run the step with the bundle that last succeeded at the previous step (`?expectedArtifactBundleId=` to refuse if it changed). Refused while blocked, while a job for the step is active, or if it would go backwards |
| POST | `/api/processing-steps/{stepId}/re-promote` | Removed — always fails with a pointer to `promote` (kept one release for old `pl` binaries; not in `api.json`) |

### Artifact Bundles

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/artifact-bundles` | List artifact bundles |
| GET | `/api/artifact-bundles/{bundleId}` | Get artifact bundle |

### Jobs

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/jobs` | List all jobs |
| GET | `/api/jobs/{id}` | Get job |
| GET | `/api/jobs/queue` | Get scheduled job queue |
| POST | `/api/jobs/{id}/cancel` | Cancel job |
| DELETE | `/api/jobs/{id}` | Delete job |

### Triggers

Triggers are defined in `.pipelines/config.yaml` and materialized by reconcile — not API-writable. Firing a webhook is operational and stays open.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/triggers` | List triggers |
| GET | `/api/triggers/{triggerId}` | Get trigger |
| POST | `/api/webhooks/{triggerId}` | Fire webhook trigger |

Trigger types:
- **production** — triggers all production steps
- **processing** — triggers a specific processing step with an artifact bundle
- **poll** — periodically GETs a URL, extracts a JSON value via dot-path, and triggers production when the value changes. Supports `$SECRET:NAME` references in headers resolved from pipeline K8s secrets.

### Secrets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/secrets` | List secret names |
| PUT | `/api/pipelines/{pipelineId}/secrets/{name}` | Set secret |
| DELETE | `/api/pipelines/{pipelineId}/secrets/{name}` | Delete secret |

### GitOps Binding

See [GitOps Configuration](#gitops-configuration). `branch` defaults to `main`, `path` to
`.pipelines`; `credentialsSecret` names the key in the pipeline's k8s secret holding the repo
read token.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/with-repo` | Create a pipeline already bound to a repo (the only way to create a pipeline) |
| GET | `/api/pipelines/{id}/binding` | Get the binding |
| GET | `/api/pipelines/{id}/binding/status` | Reconcile result/problems + live secret set/unset |

### Other

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Health check |
| GET | `/download/{asset}` | Anonymous download of a `pl` CLI binary (`pl-linux-x64`, `pl-win-x64.exe`) — served from the instance's Garage store, published by the `publish-cli` pipeline step |

## Installing the `pl` CLI

The `pl` operator CLI is built and published by this repo's own pipeline (the `publish-cli`
step) to the instance's Garage store, and served back from `GET /download/<asset>`. Because the CLI
already defaults its API URL to the same (Tailscale-reachable) host, anyone who can use `pl`
can install it from there — no GitHub release or public CDN involved.

```bash
# Linux
curl -fsSL https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/install.sh | sh
```

```powershell
# Windows
irm https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/install.ps1 | iex
```

Override the source instance with `PL_API_URL` (e.g. `https://pipelines-beta.ovea.pro`) and the
install dir with `PL_INSTALL_DIR`. linux-x64 is a Native-AOT binary; win-x64 is a self-contained
single-file `.exe` (Native AOT cannot cross-compile to Windows from the Linux build runner).

### `pl` commands

Every command accepts the global flags `--json` (machine-readable output on stdout),
`--verbose`/`-v` (diagnostics to stderr), and `--help`/`-h`. Run `pl help` for the full list or
`pl <command> --help` for one command. A few of note:

```bash
pl pipeline list                 # ID, NAME, and aggregate STATUS for every pipeline
pl pipeline list --summaries     # adds bound repo + per-step health strip
pl pipeline get <pipeline>       # show one pipeline (id or name)
pl pipeline delete <id>          # delete a pipeline (cascade-cancels its scheduled/in-progress jobs)
pl pipeline health <pipeline>    # run-based health: last run, running/held runs, blocked gates, activation
pl pipeline versions <pipeline>  # versions (commit, step versions) and which one is active
pl pipeline manifest <pipeline> [--version n]          # a version's manifest (--json: the canonical JSON)
pl pipeline switch <pipeline> <n> [--cancel-running]   # make version n active (rollback = an older n)

# v2 runs. Addresses: <pipeline>#<run>[/<step>[@<attempt>]] — pipeline by id or name (quote names
# with spaces); without @attempt a step means its latest successful attempt (logs: the latest).
pl trigger <pipeline> [--input json|@file] [--field k=v ...]   # start a run; prints <pipeline>#<run>
pl run list <pipeline>                       # runs, newest first
pl run get <pipeline>#<run>                  # input, version, stages with per-step status/reason/held/blocked
pl run cancel|rerun <pipeline>#<run>         # cancel; or a new run with the same input
pl run continue <pipeline>#<run> [--field k=v ...]   # continue, changing not-yet-consumed input fields
pl step output <pipeline>#<run>/<step>[@n]   # the attempt's output JSON
pl step files <address>                      # stored files; pl step file <address> <path> [-o out]
pl step logs <address> [--follow]            # logs (follow polls until the attempt finishes)
pl step rerun <pipeline>#<run>/<step> [--continue]    # a new attempt; --continue re-opens downstream
pl step history <pipeline>/<step>            # the step's attempts across runs, with definition changes
pl gate list <pipeline>                      # blocked steps (promotion gates, keyed on step name)
pl gate block|unblock <pipeline>/<step>      # hold runs at a step / let held runs resume

pl job list [--pipeline <id>]    # attempt-level view: every attempt (one K8s Job each), paginated
pl job get|logs|cancel <id>      # one attempt by its id

pl binding create <repo>         # create a pipeline bound to a repo (GitOps); --credentials-secret, --branch, --path, --trigger
pl binding get <pipelineId>      # show a pipeline's binding (repo@branch, trigger, deploy/sync cursors)
pl binding status <pipelineId>   # reconcile result + declared-secret set/unset state
pl binding set-credentials <pipelineId> [key]   # set (or, with no key, clear) the GitHub-token secret key
pl binding set-trigger <pipelineId> <mode>      # webhook | webhook-only | poll
pl binding reconcile <pipelineId>               # apply the bound config now, off the poll schedule
```

`pl pipeline list` reports a **status** per pipeline, computed over its runs (cancelled and
superseded runs are ignored; skipped steps never count as faults):

| Status | Meaning |
|--------|---------|
| `Healthy` | nothing running; the latest settled run succeeded |
| `Running (Healthy)` | a run is running; the latest settled run did not fail |
| `Running (Unhealthy)` | a run is running; the latest settled run failed |
| `Unhealthy` | nothing running; the latest settled run failed |
| `Idle` | no settled run and nothing running |

Each **attempt** of a step runs as one K8s Job; `pl job list` lists attempts across runs with their
run number, step name and attempt number.

#### `pl dev` (v2 local development)

`pl dev` runs a v2 TypeScript pipeline from the repo, with no service and no login. It calls the
SDK's `olve-dev`/`olve-build` through Bun. Bun is looked up in this order: `$OLVE_BUN`, then
`PATH`, then `~/.bun/bin/bun`. The SDK is the `@olve/pipelines` that the pipeline itself resolves;
override it with `$OLVE_SDK`. The pipeline directory defaults to `.pipelines` (`--dir` overrides
it). Everything is written to `<dir>/.olve/`, which is already git-ignored.

```bash
pl dev describe [--json]                         # manifest summary + describe-time checks (exit 78 on problems)
pl dev validate [--json]                         # describe, then the service's reconcile checks (needs a session; exit 78 if invalid)
pl dev run <step> --input sha=<40 hex>           # one step from source -> <dir>/.olve/work/; exit code = harness's
pl dev run build                                 # next step: inputs come from the local pool
pl dev run checkout --input-file in.json         # use input.json verbatim
pl dev run deploy-beta --secret SSH_PRIVATE_KEY="$(cat key)"   # secrets: --secret, else the environment
pl dev run build --docker                        # olve-build, then run the binary in the step's image like the pod
pl dev run build --from olve-pipelines#412       # parsed, but needs the v2 run API (not available yet)
pl dev pipeline --input sha=<sha> --input deployTo=beta [--until <step>] [--only a,b] [--yes]
```

How `pl dev run` and `pl dev pipeline` work:

- **Local pool.** `<dir>/.olve/pool.json` holds the pipeline input and each step's latest local
  outcome. A step's `input.json` is built from its manifest mappings, the same way the service
  builds it:
  - `input.*` refs read the pipeline input. That is the `--input k=v` values, typed by the input
    model, layered over the pool's previous input, with the model's defaults filling the rest.
  - Step refs read earlier steps' outputs.

  Output files are copied to `<dir>/.olve/files/0/<step>/1/files/`, which is passed to the harness
  as `OLVE_FILES_DIR`. So running steps one after another chains them, file refs included.
- **Missing inputs.** If a required input's step never ran, `pl dev run` fails with a hint to run
  that step first. If that step was skipped, an optional input is left out.
- **Secrets.** Only the step's declared secrets reach it, from `--secret` or the environment. Other
  pipeline secrets are removed from the child's environment. A declared secret that isn't set only
  produces a warning, because the handler fails only if it reads that secret.
- **`pl dev pipeline`.**
  - It runs the stages in order. Steps within a stage run one after another.
  - It evaluates `when` blocks against the input and propagates skips.
  - A failed step stops the run.
  - Before each step that declares secrets it asks for confirmation. Without a terminal it refuses
    unless you pass `--yes`.
  - A full run starts a fresh pool. `--only` reuses the pool for everything it doesn't run.
  - Failure handlers are not run.
- **`--docker`.**
  - It compiles into `<dir>/.olve/build`.
  - It extracts the glibc loader libs from the `harness-base` image once, into `<dir>/.olve/lib/<tag>/`.
  - It stages `/olve` (`step`, `lib/`, `input.json`, and `work/` and `tmp/` at mode 1777) and copies
    it into a container of the step's image.
  - The container runs `/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step run <step>`
    with `OLVE_DIR=/olve` and `TMPDIR=/olve/tmp`. The pool's files are mounted read-only.
  - It copies `/olve/work` back into `<dir>/.olve/work`.

## Build & Test

```bash
dotnet build                                                   # Build
dotnet test                                                    # Unit tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false  # Integration tests only (requires Docker)
dotnet test -p:RunIntegrationTests=true                        # All tests
dotnet run --project src/Olve.Pipelines                        # Run locally
```

Integration tests run against the live **beta** instance over authenticated HTTP (no Docker or Testcontainers); see `test/Olve.Pipelines.IntegrationTests/AppFixture.cs` for the env vars.

## Configuration

Sources in priority order (highest wins):

1. CLI args (`--Port 9090`)
2. User secrets (`dotnet user-secrets set "Key" "value"`)
3. Environment variables
4. `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Host` | `localhost` | Listen address |
| `Port` | `5000` | Listen port |
| `Auth:Authority` | — | OIDC authority |
| `Auth:Audience` | `olve-pipelines` | JWT audience |
| `Auth:SigningKey` | — | Local HS256 key (bypasses OIDC, for dev) |
| `Storage:Endpoint` | — | S3-compatible endpoint (the instance's Garage) |
| `Storage:AccessKey` | — | S3 access key |
| `Storage:SecretKey` | — | S3 secret key |
| `Storage:Bucket` | — | S3 bucket name |
| `Storage:InClusterEndpoint` | `Storage:Endpoint` | Garage endpoint as step pods reach it; pre-signed URLs are signed for this host (`Host` is in the SigV4 signature) |
| `Storage:Region` | `us-east-1` | SigV4 region for pre-signed URLs (Garage's `s3_region`) |
| `Storage:PresignExpirySeconds` | `3600` | Default pre-signed URL expiry (result PUT / file GETs get the step deadline + grace) |
| `Harness:HarnessBaseImage` | `registry.ovea.pro/olve-pipelines-v2/harness-base:0c4808c152bf` | v2 step-pod init image (pinned, never `latest`) |
| `Harness:BunImage` | `oven/bun:1.4.3-slim` | v2 build/describe Job image (glibc, never alpine) |
| `Harness:DefaultStepTimeoutSeconds` / `MaxStepTimeoutSeconds` / `StartupGraceSeconds` | `3600` / `21600` / `120` | Step Job `activeDeadlineSeconds` = min(timeout, max) + grace |
| `Harness:ScratchSizeLimit` | `1Gi` | `sizeLimit` of the step pod's `olve` emptyDir |
| `Harness:StepCpuRequest` / `StepMemoryRequest` / `StepMemoryLimit` | — | Optional step container resources |
| `Harness:BuildTimeoutSeconds` / `BuildScratchSizeLimit` | `900` / `4Gi` | Build/describe Job deadline and scratch size |
| `Harness:BuildTokenSecretKey` | `GITHUB_TOKEN` | Pipeline-secret key used (optionally) to fetch the repo tarball in build Jobs |
| `Kubernetes:Namespace` | — | K8s namespace for jobs and secrets |
| `OpenTelemetry:Endpoint` | — | OTLP endpoint (null = disabled) |
| `Logging:Console:FormatterName` | `simple` | Console formatter. Set to `json` in deployed envs for structured logs (see below) |
| `Logging:Console:Json:UseUtcTimestamp` | `false` | Emit log timestamps in UTC when using the `json` formatter |

### Logging

Logging is **backend-agnostic**: the app only writes to **stdout**. Shipping is the cluster's
job — the node's OTel Collector DaemonSet scrapes container stdout into the homelab OpenObserve
stack. The app names no log endpoint, token, or stream. (The separate OTLP *push* exporter under
`OpenTelemetry:*` is a legacy traces/metrics path and unrelated to log shipping.)

- **Format.** Deployed environments set `Logging__Console__FormatterName=json` (in `helm/values.yaml`,
  inherited by beta) so every line is structured JSON and message args (`{JobId}`, `{ElapsedMs}`, …)
  become individually queryable fields. Local `dotnet run` omits it and gets the pretty `simple`
  formatter. No application code selects the formatter — it is pure configuration.
- **Levels.** `Information` = operator-facing audit milestones (job created/completed, trigger fired,
  reconcile done); `Warning` = recoverable/degraded + **always-logged security events** (webhook
  signature mismatch, auth rejection); `Error` = an operation failed / unhandled background exception;
  `Debug`/`Trace` = loop iterations and wire detail, off in prod.
- **Result problems.** Failures are logged with `ILogger.LogProblems(...)` (in
  `src/Olve.Pipelines/Shared/ResultProblemLogExtensions.cs`), which flattens a `ResultProblem` set into
  the scalar fields `ProblemCount` / `ProblemMessages` / `MaxSeverity` instead of one opaque blob.
  Route new failure logs through it rather than a `{Problems}` placeholder.

## Client Generation

### C# ([Refitter](https://refitter.github.io/))

The `clients/Olve.Pipelines.Client/` project uses the Refitter source generator to produce a typed Refit interface from `api.json` at build time. The `.refitter` config uses `returnIApiResponse: true` so error responses don't throw exceptions.

### TypeScript ([Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview))

```bash
dotnet tool restore
dotnet kiota generate -l typescript -d api.json -c ApiClient -o clients/olve-pipelines-client-ts/src -n OlvePipelinesClient
```

## Versioning

```bash
dotnet run tools/version.cs                                          # 0.0.0-dev+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42                  # 2026.3.28.42+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42 --rid linux-x64  # artifact name
```

## Running

```bash
# Local
dotnet run --project src/Olve.Pipelines

# Kubernetes
helm install olve-pipelines helm/
```
