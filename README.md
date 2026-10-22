# Olve.Pipelines

A small CD orchestration service for a homelab. Pipelines are defined in TypeScript in the repo
they deploy (`.pipelines/pipeline.ts`): typed **models**, **steps** that consume and produce them,
**handlers** that implement the steps, and a pipeline of sequential **stages** of parallel steps.
The service compiles each definition into a versioned binary and runs every step as a Kubernetes
Job.

> **This is v2**, developed on the `v2` branch and deployed as a separate instance
> (`https://pipelines-v2-beta.ovea.pro`) next to v1 until every pipeline has migrated. v2 is fully
> breaking: v1's `config.yaml`, production/processing steps, ArtifactBundles and job groups don't
> exist here. Design: [`docs/design/pipelines-v2.md`](docs/design/pipelines-v2.md); what was built
> and the decisions taken: [`docs/design/pipelines-v2-plan.md`](docs/design/pipelines-v2-plan.md).

## Pipeline model

```ts
export default pipeline({ name: "Olve.Pipelines v2", input: M.DeployRequest })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: "beta" }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)                                      // parallel; inputs matched by model
  .when({ deployTo: "beta" }, p => p.stage(deployBeta));
```

- **Stages** run in order; the steps of a stage run in parallel; a stage is a barrier (the deploy
  waits for the tests).
- **Inputs** are matched by model from the pipeline input and earlier outputs, or wired explicitly
  with a mapper. Mappers and `when` conditions are recorded as data in a **manifest**, so the UI
  and `pl` can render wiring and run plans without running user code.
- **Triggers**: `onPush` (the bound branch moved), `onPoll` (a polled value changed), manual
  (`pl trigger`, the UI form generated from the input model).
- **Versions**: each config commit is compiled (in a sandboxed build Job) into a manifest + a single
  executable. A changed manifest becomes a new pipeline version; one version is active, and the
  service `switch`es (drain, then activate) to new versions automatically.
- **Runs** pin a version; each step execution is an **attempt** (one K8s Job). Newer automatic runs
  supersede older ones; automatic triggers are de-duplicated by mapped input; operators rerun,
  continue (with changed input) and cancel runs, and block steps (**promotion gates**).
- **Harness**: a step's image can be any x86-64 image — an init container drops the compiled binary
  and a bundled glibc loader into it.

This repo deploys itself with it: [`.pipelines/pipeline.ts`](.pipelines/pipeline.ts) builds the
`v2` branch with kaniko, runs the unit tests in parallel, and Helm-deploys v2 beta. The v1-format
[`.pipelines/config.yaml`](.pipelines/config.yaml) beside it is the paused v1 deployer, kept as the
rollback path (see the E2 notes in the plan). [`.pipelines-test/`](.pipelines-test) is the v2
end-to-end fixture.

## Adding CD to your repo

> The setup guide is **served by the running instance at [`/docs`](docs/setup/index.md)** (raw
> Markdown, with a [subject index](docs/setup/subjects.md)). This README is the codebase view.

1. Add `.pipelines/` (`models.ts`, `pipeline.ts`, `handlers/`, `package.json`, `bun.lock`) — see
   [Getting Started](docs/setup/getting-started.md) and the
   [Pipeline Reference](docs/setup/pipeline-reference.md). The SDK (`@olve/pipelines`, in
   [`sdk/`](sdk/README.md)) is not published to a registry yet, so the build Job can only resolve it
   from an `sdk/` directory at the repo root (`"@olve/pipelines": "file:../sdk"`).
2. Check it locally: `pl dev describe`, `pl dev validate`, `pl dev run <step>`.
3. `pl binding create <owner/repo> --trigger poll` — the only way to create a pipeline.
4. `pl secret set <pipelineId> <NAME>` for each declared secret.
5. `pl binding reconcile <pipelineId>`, then push. `pl binding status <pipelineId>` shows the result.

**Git-only:** there are no endpoints that change pipeline shape. Operations — trigger, cancel,
rerun, continue, gates, switch, secret values, binding trigger mode — are API-allowed.

## Project structure

```
src/Olve.Pipelines/                 API + scheduler (minimal API, Native AOT)
├── Configuration/                  Auth, telemetry, JSON, host, storage, DI wiring
├── Distribution/                   /download/{asset} (latest publish-cli files), /install.sh|.ps1
├── GitHub/                         GitHub client, push webhook payload + signature
├── Health/                         /api/health, /api/ready
├── Jobs/                           JobRunner, K8s executor, obsoletion, job (attempt) endpoints
├── Kubernetes/                     K8s client, pipeline Secrets, OpenBao / in-cluster credentials
│   └── Harness/                    step / build / map-trigger / failure-handler Job specs, result collection
├── Pipelines/                      Pipeline entity, summaries, health, storage layout
│   ├── Failures/                   onFailure dispatch, FailureContext
│   ├── Manifests/                  manifest parser, validator (reconcile checks), entity versioning, JSON Schema
│   ├── Retention/                  run/binary/leftover sweeps
│   ├── Runs/                       Run, Attempt, run/attempt services, run endpoints, views
│   ├── Scheduling/                 StageScheduler, run start (de-dup, supersede), continue/rerun, cancel, gates, switch
│   ├── Sync/                       bindings, deploy poll, reconcile, binding webhooks, config source
│   ├── Triggers/                   push / poll / manual triggers, map-trigger, input validation
│   └── Versions/                   PipelineVersion, activation (Active / Draining), version endpoints
└── Shared/                         AttachmentStore, startup runner, persistence, S3 presigning
src/Olve.Pipelines.Cli/             `pl` operator CLI (Native AOT, hand-rolled HTTP client)
sdk/                                @olve/pipelines: authoring API, harness, olve-build, olve-dev (Bun)
harness/                            harness-base init image + end-to-end validation
frontend/                           Dashboard UI (Lit + Vite + TypeScript)
clients/olve-pipelines-client-ts/   Generated TypeScript client (Kiota)
clients/Olve.Pipelines.Client/      Generated C# client (Refitter source gen)
helm/                               Helm chart (values.yaml, values-beta.yaml, values-v2-beta.yaml, values-minimal.yaml)
.pipelines/                         This repo's own pipeline (v2) + the paused v1 config.yaml
.pipelines-test/                    v2 fixture pipeline (bound on v2 beta)
docs/setup/                         Setup guide, served at /docs
docs/design/                        v2 spec, plan, spike findings
spikes/                             v2 spike code
test/Olve.Pipelines.UnitTests/      Unit tests (TUnit + Rocks)
test/Olve.Pipelines.IntegrationTests/  Integration tests against the live beta instance
tools/version.cs                    CalVer versioning script
```

## Endpoints

Reads (`GET`) are anonymous except secret listing and step-file listing/downloads. Everything else
needs a bearer token. Errors (any non-2xx but 401/403) are `application/problem+json`
`{type, title, status, detail, errors: [{code, message, field?}], traceId}`; codes `not-found` (404),
`conflict` (409), `invalid` (400), `unavailable` (503, with `Retry-After`) and `internal` (500).

### Pipelines

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines` | List pipelines |
| GET | `/api/pipelines/summary` | List-page cards: `{id, name, description, repo, health}` |
| GET | `/api/pipelines/{id}` | Get a pipeline |
| DELETE | `/api/pipelines/{id}` | Delete a pipeline (cascades to runs, versions, binding) |
| GET | `/api/pipelines/{pipelineId}/health` | Run-based health: last run, running/held runs, gates, activation |

### Bindings (GitOps)

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/with-repo` | Create a pipeline bound to a repo (`{ repo, branch?, path?, credentialsSecret?, deployTrigger? }`) — the only create |
| GET | `/api/pipelines/{pipelineId}/binding` | Get the binding |
| GET | `/api/pipelines/{pipelineId}/binding/status` | Reconcile result, commit, in-progress, resulting version, active version, problems, warnings |
| PATCH | `/api/pipelines/{pipelineId}/binding` | Set/clear the credentials secret key |
| PATCH | `/api/pipelines/{pipelineId}/binding/deploy-trigger` | `webhook` / `webhook-only` / `poll` |
| POST | `/api/pipelines/{pipelineId}/binding/reconcile` | Reconcile now (returns at once; progress in status) |
| POST | `/api/webhooks/binding/{bindingId}/github` | GitHub push webhook (HMAC-authenticated, anonymous) |

### Versions

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/versions` | Versions (commit, step/model versions) + activation |
| GET | `/api/pipelines/{pipelineId}/versions/{number}` | One version, including its canonical manifest |
| POST | `/api/pipelines/{pipelineId}/switch` | Switch the active version (`{ version, cancelRunning? }`) |
| POST | `/api/manifests/validate` | Run the reconcile checks on a manifest (`pl dev validate`); always 200 with `{ valid, manifestHash, errors, warnings }` |

### Runs

All under `/api/pipelines/{pipelineId}/runs`.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | Runs, newest first (paged) |
| POST | `/` | Start a manual run; the body is the pipeline input. `{ outcome: started \| queued, run }` |
| GET | `/{number}` | Run detail: input, pinned version + commit, links, `continuable`, stages with per-step status |
| POST | `/{number}/cancel` | Cancel the run and its scheduled + in-progress attempts |
| POST | `/{number}/rerun` | New run with the same input (`rerunOf`) |
| POST | `/{number}/continue` | Continue, `{ input: { field: value } }` merged over the stored input |
| GET | `/{number}/attempts` | All attempts of the run |
| GET | `/{number}/steps/{step}/attempts` | One step's attempts |
| POST | `/{number}/steps/{step}/rerun` | New attempt of a step (`{ continue }`) |
| POST | `/{number}/steps/{step}/cancel` | Cancel the step's scheduled/in-progress attempt (Job deleted); the run ends Cancelled once the stage settles |
| GET | `/{number}/steps/{step}/output` | Output JSON (`?attempt=`; default: latest successful) |
| GET | `/{number}/steps/{step}/files` | Stored files of an attempt (authenticated) |
| GET | `/{number}/steps/{step}/files/{*path}` | Download a file (authenticated; `dir/` → `dir.tar`) |
| GET | `/{number}/steps/{step}/logs` | Attempt log (live pod log while running) |
| GET | `/{number}/steps/{step}/logs/stream` | The attempt's log as SSE (`?attempt=`; default: latest): `log` `{text, reset}` chunks, then `end` `{status}` once terminal |

### Steps and gates

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/steps/{step}/history` | A step's attempts across runs, with step-version change markers |
| GET | `/api/pipelines/{pipelineId}/gates` | Blocked step names |
| POST | `/api/pipelines/{pipelineId}/steps/{step}/block` | Block a step (runs reaching it are held) |
| POST | `/api/pipelines/{pipelineId}/steps/{step}/unblock` | Unblock (held runs resume) |

### Attempts across pipelines

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/attempts?status=active` | What's running: every scheduled/in-progress attempt, oldest first, as `{pipelineId, pipelineName, run, step, attempt, status, createdAt, startedAt}` |

### Secrets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/secrets` | `[{name, description, declaredBy, isSet}]`: secrets the active version (steps, onFailure handlers, poll triggers) or the binding uses, plus set-but-unused ones; `isSet` null when K8s is unreadable. Values are never returned |
| PUT | `/api/pipelines/{pipelineId}/secrets/{name}` | Set a value |
| DELETE | `/api/pipelines/{pipelineId}/secrets/{name}` | Delete |
| POST | `/api/pipelines/{pipelineId}/secrets/copy` | Copy secrets from another pipeline on this instance, server-side (`{ sourcePipelineId, names? (omit = all), targetName? (one name only), overwrite? }` → `{ copied }`, names only; refuses existing names without `overwrite`). Caveat: whoever can write the target pipeline can read the copied values from its steps |

### Live events (SSE)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/events?pipeline=<id>` | `text/event-stream` of change notifications (omit `pipeline` for all). Anonymous |

Each event is a small "something changed" notice (camelCase JSON); the client re-fetches the view it affects:

| Event | Data | Fired when |
|-------|------|------------|
| `run` | `{pipelineId, run, status}` | A run is created, changes (status, held, resumed) or is deleted (`status: "Deleted"`) |
| `attempt` | `{pipelineId, run, step, attempt, status}` | An attempt is created or changes status (`scheduled`, `in-progress`, `succeeded`, …) |
| `version` | `{pipelineId, version, change}` | `added` (new version), `active` (switched / switch done), `draining` (switch to `version` started) |
| `gate` | `{pipelineId, blockedSteps}` | A step is blocked or unblocked (the full blocked list) |
| `binding` | `{pipelineId, state}` | The binding changes: `reconciling`, then `Success` / `Error`; also `removed` |
| `pipeline` | `{pipelineId, change}` | `added`, `updated`, `removed` |
| `hello` | `{lastEventId}` | First event of every connection; carries `retry: 5000` |
| `resync` | `{reason}` | The client reconnected with a stale `Last-Event-ID` (or across a restart): refetch everything |
| `ping` | (empty) | Keep-alive every 15 s while idle |

Ids are `<boot>-<seq>`; there is no replay. A client that falls 256 events behind is disconnected (its
`EventSource` reconnects and gets `resync`). Streams end when the service shuts down.

### Other

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Liveness |
| GET | `/api/ready` | Readiness (persisted state loaded) |
| GET | `/api/config/frontend` | OIDC authority + client id for the UI and `pl login` |
| GET, HEAD | `/download/{asset}` | `pl` binary (`pl-linux-x64`, `pl-win-x64.exe`): an output file of the latest successful `publish-cli` attempt of the pipeline named by `Distribution:CliPipeline` (404 until one exists). Anonymous; retention keeps that run |
| GET | `/install.sh`, `/install.ps1` | The `pl` install scripts, defaulting to this instance (`Links:BaseUrl`, else the request origin) |
| GET | `/docs/<page>.md` | The setup guide (raw Markdown), `/docs/llms.txt` |

## The `pl` CLI

### Installing

The v2 self-deploy's `publish-cli` step builds the v2 `pl` of every deployed commit, and v2 beta
serves it. Install it from there:

```bash
curl -fsSL https://pipelines-v2-beta.ovea.pro/install.sh | PL_INSTALL_DIR=~/.local/opt/pl-v2 sh
export PATH=~/.local/opt/pl-v2:$PATH PIPELINES_CONFIG=~/.pl-v2   # v2 pl first; keep its session apart from v1's ~/.pl
pl --version                                                    # the publishing run's version stamp
pl login --api-url https://pipelines-v2-beta.ovea.pro           # add --device over SSH
```

Windows: `irm https://pipelines-v2-beta.ovea.pro/install.ps1 | iex`. The binaries alone:
`/download/pl-linux-x64` (Native AOT) and `/download/pl-win-x64.exe` (self-contained single-file).
The scripts in the repo (and `install.sh` from raw GitHub) default to v1 prod, which serves **v1's**
`pl`; an instance serving them rewrites the default to itself. `PL_API_URL` / `PL_INSTALL_DIR`
override. From source: `dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl-v2`.

`pl login` caches the token and the API URL in `~/.pl` (or `$PIPELINES_CONFIG`); `--api-url` /
`PIPELINES_API_URL` override the URL per command, `--token` passes a bearer directly.

### Commands

Global flags: `--json`, `--verbose`/`-v`, `--help`/`-h`. `pl help`, `pl <noun> --help` and
`pl <command> --help` print the details.

```bash
# pipelines — <pipeline> is an id or a name (quote names with spaces)
pl pipeline list [--summaries]                         # health, last run, active version, running/held, closed gates
pl pipeline get <pipeline>
pl pipeline delete <pipeline>                          # also deletes its binding, runs and secrets
pl pipeline health <pipeline>
pl pipeline versions <pipeline>                        # > marks the version runs start on
pl pipeline manifest <pipeline> [--version <n>] [--json]
pl pipeline switch <pipeline> <version> [--cancel-running]   # rollback = an older version

# runs. Addresses: <pipeline>#<run>[/<step>[@<attempt>]]
pl trigger <pipeline> [--input <json|@file|@->] [--field k=v ...]   # prints <pipeline>#<run>
pl run list <pipeline> [--page <n>] [--page-size <n>]
pl run get <pipeline>#<run>
pl run cancel <pipeline>#<run>
pl run rerun <pipeline>#<run>
pl run continue <pipeline>#<run> [--field k=v ...] [--input <json|@file|@->]

# steps of a run (without @attempt: the latest successful attempt; logs: the latest attempt)
pl step output <pipeline>#<run>/<step>[@<attempt>]
pl step files <pipeline>#<run>/<step>[@<attempt>]
pl step file <pipeline>#<run>/<step>[@<attempt>] <path> [-o <file>]
pl step logs <pipeline>#<run>/<step>[@<attempt>] [--follow] [--interval <seconds>]
pl step rerun <pipeline>#<run>/<step> [--continue]
pl step cancel <pipeline>#<run>/<step>                 # the run ends Cancelled once the stage settles
pl step history <pipeline>/<step> [--limit <n>]

# promotion gates
pl gate list <pipeline>
pl gate block <pipeline>/<step>
pl gate unblock <pipeline>/<step>

# what's running (scheduled/in-progress attempts across pipelines)
pl attempts

# live change events (GET /api/events) until Ctrl+C
pl watch [<pipeline>] [--json]

# secrets and bindings
pl secret list <pipeline>                              # declared-by and set/unset per secret
pl secret set <pipeline> <name> [--from-env <VAR> | --from-file <path>]   # default: stdin, verbatim
pl secret delete <pipeline> <name>
pl secret copy <source-pipeline>[/<name>] <target-pipeline> [--name <target-name>] [--overwrite]   # server-side; no /<name> = all
pl binding create <owner/repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>] [--trigger webhook|webhook-only|poll]
pl binding get|status|reconcile <pipeline>
pl binding set-credentials <pipeline> [key]
pl binding set-trigger <pipeline> <webhook|webhook-only|poll>

# local development (no service, except dev validate)
pl dev describe [--dir <path>] [--json]
pl dev validate [--dir <path>] [--json]
pl dev run <step> [--input k=v ...] [--input-file <f>] [--secret NAME=value ...] [--docker] [--json]
pl dev pipeline [--input k=v ...] [--until <step>] [--only a,b] [--secret NAME=value ...] [--docker] [--yes]

# installation
pl bootstrap / pl teardown                             # cold install / remove (see AGENTS.md)
```

`pl dev` calls the SDK's `olve-dev` / `olve-build` through Bun (`$OLVE_BUN`, then `PATH`, then
`~/.bun/bin/bun`; SDK override `$OLVE_SDK`) and writes to `<dir>/.olve/`. `pl dev run --from
<pipeline>#<run>` is parsed but not implemented yet. Details:
[docs/setup/local-development.md](docs/setup/local-development.md).

## Build & test

```bash
dotnet build                                                   # Build
dotnet test                                                    # Unit tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false  # Integration tests only
dotnet test -p:RunIntegrationTests=true                        # All tests
dotnet run --project src/Olve.Pipelines                        # Run locally

cd sdk && bun install && bunx tsc --noEmit -p . && bun test    # SDK: type-check + compile-fail cases + runtime tests
cd frontend && npm install && npm run build                    # Frontend (tsc + vite)
```

Integration tests run against the live v1 **beta** instance over authenticated HTTP; see
`test/Olve.Pipelines.IntegrationTests/AppFixture.cs` for the env vars. They don't cover v2 yet; v2
features were verified live on v2 beta against the `.pipelines-test/` fixture (see the plan).

## Configuration

Sources in priority order (highest wins): CLI args (`--Port 9090`), user secrets
(`dotnet user-secrets set "Key" "value"`), environment variables (`Storage__Bucket`),
`appsettings.json`.

| Key | Default | Description |
|-----|---------|-------------|
| `Host` / `Port` | `localhost` / `5000` | Listen address |
| `Auth:Authority` | — | OIDC authority |
| `Auth:Audience` | `olve-pipelines` | JWT audience |
| `Auth:SigningKey` | — | Local HS256 key (bypasses OIDC, for dev) |
| `Auth:Disable` | `false` | Disable auth (local dev, minimal install) |
| `Auth:FrontendAuthority` / `Auth:FrontendAudience` | — | OIDC authority and client id served to the UI and `pl login` |
| `Storage:Mode` | `Persistent` | `Ephemeral` = in-memory only |
| `Storage:Endpoint` | — | The instance's Garage (S3-compatible) endpoint |
| `Storage:AccessKey` / `Storage:SecretKey` | — | S3 credentials |
| `Storage:Bucket` | `olve-pipelines` | Bucket |
| `Storage:InClusterEndpoint` | `Storage:Endpoint` | Garage endpoint as step pods reach it; pre-signed URLs are signed for this host (`Host` is in the SigV4 signature) |
| `Storage:Region` | `us-east-1` | SigV4 region for pre-signed URLs (Garage's `s3_region`) |
| `Storage:PresignExpirySeconds` | `3600` | Default pre-signed URL expiry (step URLs get the step deadline + grace) |
| `Kubernetes:Namespace` | — | Runner namespace for step Jobs and pipeline Secrets |
| `Kubernetes:AuthMode` | OpenBao | `InCluster` = the pod's ServiceAccount; otherwise OpenBao (`Kubernetes:OpenBaoUrl`) |
| `Kubernetes:RuntimeClassName` | — | RuntimeClass for step pods (`gvisor`); empty = runc |
| `Harness:HarnessBaseImage` | `registry.ovea.pro/olve-pipelines-v2/harness-base:0c4808c152bf` | Step-pod init image (pinned, never `latest`) |
| `Harness:BunImage` | `oven/bun:1.4.3-slim` | Build/describe Job image (glibc, never alpine) |
| `Harness:DefaultStepTimeoutSeconds` / `MaxStepTimeoutSeconds` / `StartupGraceSeconds` | `3600` / `21600` / `120` | Step Job `activeDeadlineSeconds` = min(timeout, max) + grace |
| `Harness:UrlExpiryGraceSeconds` | `600` | Added to the deadline for step pre-signed URLs |
| `Harness:ScratchSizeLimit` | `1Gi` | `sizeLimit` of the step pod's `/olve` emptyDir |
| `Harness:StepCpuRequest` / `StepMemoryRequest` / `StepMemoryLimit` | — | Optional step container resources |
| `Harness:BuildTimeoutSeconds` / `BuildScratchSizeLimit` | `900` / `4Gi` | Build/describe Job deadline and scratch size |
| `Harness:BuildCpuRequest` / `BuildMemoryRequest` / `BuildMemoryLimit` | `500m` / `512Mi` / `3Gi` | Build/describe Job resources |
| `Harness:BuildTokenSecretKey` | `GITHUB_TOKEN` | Pipeline-secret key used for the repo tarball when the binding names no credentials key |
| `Reconcile:PollIntervalSeconds` | `900` | Deploy poll interval per binding |
| `Webhooks:PublicBaseUrl` | — | Public origin GitHub posts push webhooks to; empty = no webhook registration (v2 beta) |
| `Links:BaseUrl` | `Webhooks:PublicBaseUrl` | Base of the run/log links in a failure handler's `FailureContext` |
| `Retention:Enabled` / `Retention:DryRun` | `true` / `true` | Retention sweeps; **dry run by default** (logs only) — set `Retention__DryRun=false` to delete |
| `Retention:KeepLatestRuns` / `Retention:MinRunAgeDays` | `20` / `183` | A run is deleted only when beyond the latest N **and** older than this |
| `Retention:BuildGraceDays` | `7` | Unused compiled binaries are deleted after this |
| `Retention:OrphanGraceDays` / `ResultArchiveGraceHours` | `7` / `24` | Leftover objects of deleted runs / stale `result.tar.gz` |
| `Retention:ClusterGraceMinutes` / `FinishedJobMaxAgeDays` | `60` / `7` | URL Secrets and leaked step Jobs / TTL-less finished Jobs |
| `Retention:IntervalHours` / `StartupDelayMinutes` | `24` / `2` | Sweep schedule |
| `Distribution:CliPipeline` / `Distribution:CliStep` | — / `publish-cli` | Pipeline (name or id) whose latest successful `CliStep` attempt `/download` serves; unset = nothing served. Retention keeps that run |
| `OpenTelemetry:Endpoint` | — | OTLP endpoint (null = disabled) |
| `Logging:Console:FormatterName` | `simple` | Console formatter. `json` in deployed envs (see below) |
| `Logging:Console:Json:UseUtcTimestamp` | `false` | UTC timestamps with the `json` formatter |

### Logging

Logging is **backend-agnostic**: the app only writes to **stdout**. Shipping is the cluster's
job — the node's OTel Collector DaemonSet scrapes container stdout into the homelab OpenObserve
stack. The app names no log endpoint, token, or stream. (The separate OTLP *push* exporter under
`OpenTelemetry:*` is a legacy traces/metrics path and unrelated to log shipping.)

- **Format.** Deployed environments set `Logging__Console__FormatterName=json` (in `helm/values.yaml`,
  inherited by beta) so every line is structured JSON and message args (`{RunNumber}`, `{StepName}`, …)
  become individually queryable fields. Local `dotnet run` gets the pretty `simple` formatter. No
  application code selects the formatter — it is pure configuration.
- **Levels.** `Information` = operator-facing audit milestones (run started, attempt finished, trigger
  fired, reconcile done); `Warning` = recoverable/degraded + **always-logged security events** (webhook
  signature mismatch, auth rejection); `Error` = an operation failed / unhandled background exception;
  `Debug`/`Trace` = loop iterations and wire detail, off in prod.
- **Result problems.** Failures are logged with `ILogger.LogProblems(...)` (in
  `src/Olve.Pipelines/Shared/ResultProblemLogExtensions.cs`), which flattens a `ResultProblem` set into
  the scalar fields `ProblemCount` / `ProblemMessages` / `MaxSeverity` instead of one opaque blob.

## Client generation

`api.json` is regenerated on every `dotnet build`. After an API change, regenerate both clients.

### TypeScript ([Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview))

```bash
dotnet tool restore
dotnet kiota generate -l typescript -d api.json -o clients/olve-pipelines-client-ts/src -n OlvePipelinesClient --clean-output
cd clients/olve-pipelines-client-ts && npm install
```

Kiota is pinned in `.config/dotnet-tools.json` (1.36.1; 1.30 emits a `tsc` error on nullable
polymorphic members). Error responses are a typed `ApiProblem`; the frontend still sends actions
through `apiFetch` (see [frontend/README.md](frontend/README.md#api-usage)).

### C# ([Refitter](https://refitter.github.io/))

`clients/Olve.Pipelines.Client/` generates a Refit interface from `api.json` at build time
(`.refitter` with `returnIApiResponse: true`, so error responses don't throw). The `pl` CLI does not
use it (Refit can't Native-AOT here); `pl` has a hand-rolled client in `src/Olve.Pipelines.Cli/Api/`.

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

```

On Kubernetes the chart in `helm/` is deployed by the pipeline itself: v2 beta is the release
`olve-pipelines-v2` in `apps-beta`, installed by the `deploy-beta` step (`steps.helmDeployOverSsh`,
values `values-beta.yaml` then `values-v2-beta.yaml`). A cold install of a minimal controller is
`pl bootstrap` (see [AGENTS.md](AGENTS.md)).
