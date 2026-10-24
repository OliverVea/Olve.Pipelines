# Olve.Pipelines

A small CD orchestration service for a homelab. Pipelines are defined in TypeScript in the repo
they deploy (`.pipelines/pipeline.ts`): typed **models**, **steps** that consume and produce them,
**handlers** that implement the steps, and a pipeline of sequential **stages** of parallel steps.
The service compiles each definition into a versioned binary and runs every step as a Kubernetes
Job.

> **This is v2**, developed on the `v2` branch and deployed as separate instances (v2 prod
> `https://pipelines-v2-private.ovea.pro`, v2 beta `https://pipelines-v2-beta.ovea.pro`) next to v1
> until every pipeline has migrated. v2 is fully
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
  service `switch`es to new versions automatically (runs already going finish on theirs).
- **Runs** pin a version; each step execution is an **attempt** (one K8s Job). Runs flow on concurrently and
  meet only per step (one in progress, the newest waiting one wins); automatic triggers are de-duplicated by mapped input; operators rerun,
  continue (with changed input) and cancel runs, and can **block** a step (an operator hold, used
  sparingly while working on a pipeline).
- **Harness**: a step's image can be any x86-64 image — an init container drops the compiled binary
  and a bundled glibc loader into it.

This repo deploys itself with it: [`.pipelines/pipeline.ts`](.pipelines/pipeline.ts) builds the
`v2` branch with kaniko, runs the unit tests in parallel, Helm-deploys v2 beta, smoke-tests it and then
deploys v2 prod, which hosts the pipeline. The v1-format
[`.pipelines/config.yaml`](.pipelines/config.yaml) beside it is the paused v1 deployer, kept as the
rollback path (see the E2 notes in the plan). [`.pipelines-test/`](.pipelines-test) is the v2
end-to-end fixture.

## Adding CD to your repo

> The setup guide is **served by the running instance at [`/docs`](docs/setup/index.md)** (raw
> Markdown, with a [subject index](docs/setup/subjects.md)). This README is the codebase view.

1. Add `.pipelines/` (`models.ts`, `pipeline.ts`, `handlers/`, `package.json`, `bun.lock`) — see
   [Getting Started](docs/setup/getting-started.md) and the
   [Pipeline Reference](docs/setup/pipeline-reference.md). The SDK (`@olivervea/pipelines`, in
   [`sdk/`](sdk/README.md)) is not published to a registry yet, so the build Job can only resolve it
   from an `sdk/` directory at the repo root (`"@olivervea/pipelines": "file:../sdk"`).
2. Check it locally: `pl dev describe`, `pl dev validate`, `pl dev run <step>`.
3. `pl binding create <owner/repo>` — the only way to create a pipeline.
4. `pl secret set <pipelineId> <NAME>` for each declared secret.
5. `pl binding reconcile <pipelineId>`, then push. `pl binding status <pipelineId>` shows the result.

**Git-only:** there are no endpoints that change pipeline shape. Operations — trigger, cancel,
rerun, continue, blocking steps, switch, secret values, binding trigger mode — are API-allowed.

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
│   ├── Scheduling/                 StageScheduler, run start (de-dup), continue/rerun, cancel, blocked steps, switch
│   ├── Sync/                       bindings, deploy poll, reconcile, binding webhooks, config source
│   ├── Triggers/                   push / poll / manual triggers, map-trigger, input validation
│   └── Versions/                   PipelineVersion, activation (Active / Draining), version endpoints
└── Shared/                         AttachmentStore, startup runner, persistence, S3 presigning
src/Olve.Pipelines.Cli/             `pl` operator CLI (Native AOT, hand-rolled HTTP client)
sdk/                                @olivervea/pipelines: authoring API, harness, olve-build, olve-dev (Bun)
harness/                            harness-base init image + end-to-end validation
frontend/                           Dashboard UI (Lit + Vite + TypeScript)
clients/olve-pipelines-client-ts/   Generated TypeScript client (Kiota)
helm/                               Helm chart (values.yaml, values-beta.yaml, values-v2-beta.yaml, values-v2-prod.yaml, values-minimal.yaml)
.pipelines/                         This repo's own pipeline (v2) + the paused v1 config.yaml
.pipelines-test/                    v2 fixture pipeline (bound on v2 beta)
docs/setup/                         Setup guide, served at /docs
docs/design/                        v2 spec, plan, spike findings (spike code: git history, `d269107:spikes/`)
test/Olve.Pipelines.UnitTests/      Unit tests (TUnit + Rocks)
tools/version.cs                    CalVer versioning script
```

## Endpoints

Everything needs a bearer token except health/ready, `/api/config` (frontend OIDC settings), `/download`
and the install scripts, the docs, and the webhook receivers (HMAC or trigger-secret verified).
`Auth__AnonymousReads=true` (v2 beta only) opens the reads (`GET`), except secret listing and step-file
listing/downloads. Errors (any non-2xx but 401/403) are `application/problem+json`
`{type, title, status, detail, errors: [{code, message, field?}], traceId}`; codes `not-found` (404),
`conflict` (409), `invalid` (400), `unavailable` (503, with `Retry-After`) and `internal` (500).

### Pipelines

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines` | List pipelines |
| GET | `/api/pipelines/summary` | List-page cards: `{id, name, description, repo, health}` |
| GET | `/api/pipelines/{id}` | Get a pipeline |
| DELETE | `/api/pipelines/{id}` | Delete a pipeline (cascades to runs, versions, binding) |
| GET | `/api/pipelines/{pipelineId}/health` | Run-based health: last run, running/held runs, blocked steps, activation |

### Bindings (GitOps)

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/with-repo` | Create a pipeline bound to a repo (`{ repo, branch?, path?, credentialsSecret? }`) — the only create |
| GET | `/api/pipelines/{pipelineId}/binding` | Get the binding |
| GET | `/api/pipelines/{pipelineId}/binding/status` | Reconcile result, commit, in-progress, resulting version, active version, problems, warnings |
| PATCH | `/api/pipelines/{pipelineId}/binding` | Set/clear the credentials secret key |
| POST | `/api/pipelines/{pipelineId}/binding/reconcile` | Reconcile now (returns at once; progress in status) |
| POST | `/api/webhooks/binding/{bindingId}/github` | GitHub push webhook (HMAC-authenticated, anonymous) |
| POST | `/api/hooks/{pipelineId}/{trigger}` | Manifest `onWebhook` trigger delivery (verified with the trigger's secret, anonymous; 202, mapped in the background) |

### Versions

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/versions` | Versions (commit, step/model versions) + activation |
| GET | `/api/pipelines/{pipelineId}/versions/{number}` | One version, including its canonical manifest |
| POST | `/api/pipelines/{pipelineId}/switch` | Switch the active version (`{ version, cancelRunning? }`); 409 with the reasons when the config document or a secret it references does not satisfy that version |
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

### Steps and blocking

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/steps/{step}/history` | A step's attempts across runs, with step-version change markers |
| GET | `/api/pipelines/{pipelineId}/blocked-steps` | Blocked step names |
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
| DELETE | `/api/pipelines/{pipelineId}/secrets/{name}` | Delete (`?force=true` for a secret the current config references; 409 without). Setting or deleting re-checks a pending version |
| GET | `/api/pipelines/{pipelineId}/config[?snapshot=N]` | The config document: `{snapshot: {number, author, createdAt, restoredFrom}, yaml}` (YAML as saved; `yaml: ""` before the first save) |
| PUT | `/api/pipelines/{pipelineId}/config` | Save `{yaml}` as the next snapshot; 400 with each problem when the active version rejects it. → `{snapshot, active, pending, activatedVersion}` (checks against the active and the pending version) |
| GET | `/api/pipelines/{pipelineId}/config/history` | Snapshots, newest first |
| POST | `/api/pipelines/{pipelineId}/config/rollback` | `{snapshot: N}`: save N's content again as the newest |
| GET | `/api/pipelines/{pipelineId}/config/check[?version=V]` | The current document against V (default: pending, else active): `{valid, satisfied, problems, referencedSecrets, missingSecrets, changedDefaults, effective}` |
| POST | `/api/pipelines/{pipelineId}/secrets/copy` | Copy secrets from another pipeline on this instance, server-side (`{ sourcePipelineId, names? (omit = all), targetName? (one name only), overwrite? }` → `{ copied }`, names only; refuses existing names without `overwrite`). Caveat: whoever can write the target pipeline can read the copied values from its steps |

### Live events (SSE)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/events?pipeline=<id>` | `text/event-stream` of change notifications (omit `pipeline` for all). Needs a session |

Each event is a small "something changed" notice (camelCase JSON); the client re-fetches the view it affects:

| Event | Data | Fired when |
|-------|------|------------|
| `run` | `{pipelineId, run, status}` | A run is created, changes (status, held, resumed) or is deleted (`status: "Deleted"`) |
| `attempt` | `{pipelineId, run, step, attempt, status}` | An attempt is created or changes status (`scheduled`, `in-progress`, `succeeded`, …) |
| `version` | `{pipelineId, version, change}` | `added` (new version), `active` (switched / switch done), `draining` (switch to `version` started) |
| `blocked` | `{pipelineId, blockedSteps}` | A step is blocked or unblocked (the full blocked list) |
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
| GET | `/api/health` | Liveness: `{ status, image, version }` (`image` from `Deployment:Image`, set by the compose deployer) |
| GET | `/api/ready` | Readiness (persisted state loaded) |
| GET | `/api/config/frontend` | OIDC authority + client id for the UI and `pl login` |
| GET, HEAD | `/download/{asset}` | `pl` binary (`pl-linux-x64`, `pl-win-x64.exe`): an output file of the latest successful `publish-cli` attempt of the pipeline named by `Distribution:CliPipeline` (404 until one exists). Anonymous; retention keeps that run |
| GET | `/install.sh`, `/install.ps1` | The `pl` install scripts, defaulting to this instance (`Links:BaseUrl`, else the request origin) |
| GET | `/docs/<page>.md` | The setup guide (raw Markdown), `/docs/llms.txt` |

## The `pl` CLI

### Installing

The v2 self-deploy's `publish-cli` step builds the v2 `pl` of every commit deployed to prod, and v2
prod serves it. Install it from there:

```bash
curl -fsSL https://pipelines-v2-private.ovea.pro/install.sh | sh   # to ~/.local/bin/pl (PL_INSTALL_DIR overrides)
pl --version                                                       # the publishing run's version stamp
pl login --api-url https://pipelines-v2-private.ovea.pro           # profile 'default'; add --device over SSH
pl login --profile beta --api-url https://pipelines-v2-beta.ovea.pro
```

Windows: `irm https://pipelines-v2-private.ovea.pro/install.ps1 | iex`. The binaries alone:
`/download/pl-linux-x64` (Native AOT) and `/download/pl-win-x64.exe` (self-contained single-file).
The scripts in the repo (and `install.sh` from raw GitHub) default to v1 prod, which serves **v1's**
`pl`; an instance serving them rewrites the default to itself. `PL_API_URL` / `PL_INSTALL_DIR`
override. From source: `dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl-v2`.

`pl login` caches the token and the API URL in a **profile** of `~/.pl` (or `$PIPELINES_CONFIG`):
`default`, or the one named by `--profile <name>` / `PIPELINES_PROFILE`. Every command uses the
selected profile; `pl profile list` shows them. `--api-url` / `PIPELINES_API_URL` override the URL per
command, `--token` passes a bearer directly. v1's `pl` doesn't read profiles: give it its own
`PIPELINES_CONFIG`.

### Commands

Global flags: `--json`, `--verbose`/`-v`, `--help`/`-h`. `pl help`, `pl <noun> --help` and
`pl <command> --help` print the details.

```bash
# pipelines — <pipeline> is an id or a name (quote names with spaces)
pl pipeline list [--summaries]                         # health, last run, active version, running/held, blocked steps
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

# blocking steps (an operator hold; use sparingly)
pl step blocked <pipeline>
pl step block <pipeline>/<step>
pl step unblock <pipeline>/<step>

# what's running (scheduled/in-progress attempts across pipelines)
pl attempts

# live change events (GET /api/events) until Ctrl+C
pl watch [<pipeline>] [--json]

# block until something happens (re-checks on live events); exit 0 ok, 1 failed, 124 --timeout
pl wait run <pipeline>#<run>[/<step>] [--until finished|started|held] [--timeout 45m]
pl wait push <pipeline> <sha> [--until started|finished]   # prints the run's address: addr=$(pl wait push p <sha>)
pl wait reconcile <pipeline> <sha>                         # the binding applied that config commit

# secrets and bindings
pl secret list <pipeline>                              # declared-by and set/unset per secret
pl secret set <pipeline> <name> [--from-env <VAR> | --from-file <path>]   # default: stdin, verbatim
pl secret delete <pipeline> <name> [--force]           # --force: a secret the current config references
pl secret copy <source-pipeline>[/<name>] <target-pipeline> [--name <target-name>] [--overwrite]   # server-side; no /<name> = all
pl binding create <owner/repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>]
pl binding get|status|reconcile <pipeline>
pl binding set-credentials <pipeline> [key]

# config (one YAML document per pipeline, saved as immutable snapshots)
pl config get <pipeline> [--snapshot N]                # the YAML as saved
pl config set <pipeline> -f app.yml                    # validate against the active version, save as config #N+1
pl config history <pipeline>
pl config rollback <pipeline> <N>                      # save snapshot N again as the newest
pl config check <pipeline> [--version V]               # problems, missing secrets, changed defaults; exit 1 if V can't activate

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
dotnet test                                                    # Tests
dotnet run --project src/Olve.Pipelines                        # Run locally

cd sdk && bun install && bunx tsc --noEmit -p . && bun test    # SDK: type-check + compile-fail cases + runtime tests
cd frontend && npm install && npm run build                    # Frontend (tsc + vite)
```

Every deploy checks the fresh v2 beta in the `beta-test` step (`.pipelines/handlers/beta-test.ts`):
readiness, the API's reads and error shape, that an anonymous mutation is refused, the event stream,
the frontend bundle and the install script.

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
| `Auth:RequireHttpsMetadata` | `true` | `false` allows a plain-HTTP OIDC provider (the local compose stack's Dex) |
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
| `Kubernetes:StepServiceAccount` | — | ServiceAccount (in the runner namespace) whose token steps that declare `clusterAccess` get (`steps.helmDeploy` in-cluster); empty = no step pod gets a token |
| `Harness:HarnessBaseImage` | `ghcr.io/olivervea/olve-pipelines-harness-base:src-…` (`HarnessImage.cs`) | Step-pod init image (pinned, never `latest`). The chart sets it from `harness.image`, which the deploy fills from the `publish-harness` step |
| `Harness:BunImage` | `oven/bun:1.4.3-slim` | Build/describe Job image (glibc, never alpine) |
| `GitHub:ApiUrl` / `GitHub:CodeloadUrl` | `https://api.github.com` / `https://codeload.github.com` | Where bindings read config cursors and the build Job fetches tarballs (GitHub Enterprise, or a local stand-in for tests) |
| `Harness:DefaultStepTimeoutSeconds` / `MaxStepTimeoutSeconds` / `StartupGraceSeconds` | `3600` / `21600` / `120` | Step Job `activeDeadlineSeconds` = min(timeout, max) + grace |
| `Harness:UrlExpiryGraceSeconds` | `600` | Added to the deadline for step pre-signed URLs |
| `Harness:ScratchSizeLimit` | `1Gi` | `sizeLimit` of the step pod's `/olve` emptyDir |
| `Harness:StepCpuRequest` / `StepMemoryRequest` / `StepMemoryLimit` | — | Optional step container resources |
| `Harness:BuildTimeoutSeconds` / `BuildScratchSizeLimit` | `900` / `4Gi` | Build/describe Job deadline and scratch size |
| `Harness:BuildCpuRequest` / `BuildMemoryRequest` / `BuildMemoryLimit` | `500m` / `512Mi` / `3Gi` | Build/describe Job resources |
| `Harness:BuildTokenSecretKey` | `GITHUB_TOKEN` | Pipeline-secret key used for the repo tarball when the binding names no credentials key |
| `Reconcile:PollIntervalSeconds` | `900` | Deploy poll interval per binding |
| `Webhooks:PublicBaseUrl` | — | Public origin GitHub posts push webhooks to; empty = no webhook registration (both v2 instances) |
| `Links:BaseUrl` | `Webhooks:PublicBaseUrl` | Base of the run/log links in a failure handler's `FailureContext` |
| `Retention:Enabled` / `Retention:DryRun` | `true` / `true` | Retention sweeps; **dry run by default** (logs only) — set `Retention__DryRun=false` to delete |
| `Retention:KeepLatestRuns` / `Retention:MinRunAgeDays` | `20` / `183` | A run is deleted only when beyond the latest N **and** older than this |
| `Retention:BuildGraceDays` | `7` | Unused compiled binaries are deleted after this |
| `Retention:OrphanGraceDays` / `ResultArchiveGraceHours` | `7` / `24` | Leftover objects of deleted runs / stale `result.tar.gz` |
| `Retention:ClusterGraceMinutes` / `FinishedJobMaxAgeDays` | `60` / `7` | URL Secrets and leaked step Jobs / TTL-less finished Jobs |
| `Retention:IntervalHours` / `StartupDelayMinutes` | `24` / `2` | Sweep schedule |
| `Distribution:CliPipeline` / `Distribution:CliStep` | — / `publish-cli` | Pipeline (name or id) whose latest successful `CliStep` attempt `/download` serves; unset = nothing served. Retention keeps that run |
| `Distribution:CliPromotedBy` | — | Only runs in which this step (e.g. `promote`) also succeeded count for `/download` |
| `Distribution:LocalDirectory` | — | Serve `/download` assets from this directory when it has them (the compose stack builds `pl` from source) |
| `Deployment:Image` | — | The image this instance runs, reported by `/api/health`. The compose deployer sets it (`Deployment__Image`) when it recreates the container |
| `OpenTelemetry:Endpoint` | — | OTLP endpoint; unset or empty = no exporter |
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
- **Requests.** Every `/api` request logs one line, `{Method} {Path} {StatusCode} {Elapsed}ms by {User}`
  (the token's `preferred_username`, else its subject, else `anonymous`), so a cancel or switch
  can be traced to who sent it.
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

### C#

There is no generated C# client. `pl` has a hand-rolled AOT-safe client in `src/Olve.Pipelines.Cli/Api/`
(Refit can't Native-AOT here); add each new endpoint there.

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

On Kubernetes the chart in `helm/` is deployed by the pipeline itself (`steps.helmDeployOverSsh` or `steps.helmDeployInCluster`, selected by its config):
v2 beta is the release `olve-pipelines-v2` in `apps-beta`, installed by `deploy-beta` (values
`values-beta.yaml` then `values-v2-beta.yaml`); v2 prod is `olve-pipelines-v2` in `apps`, installed
by `deploy-prod` (`values.yaml` then `values-v2-prod.yaml`). A cold install of a minimal controller is
`pl bootstrap` (see [AGENTS.md](AGENTS.md)).

### Running locally with Docker Compose

`docker-compose.yml` runs the full application as a throwaway instance, for example for agent evals.
Everything is built from this checkout, and nothing talks to the homelab. It runs:

- **The server image**, built from the root `Dockerfile`. Dashboard, API and `/docs` are at
  <http://localhost:5080>.
- **Garage** for storage, as in the chart.
- **A single-node k3s** in a privileged container. It runs the build Jobs and the step Jobs.
- **The step pods' init image.** `harness-build` builds it from `harness/base` with kaniko and
  pushes it to a password-protected local `registry:2`. k3s pulls it from there over plain HTTP.
- **`pl`.** `pl-build` builds it from source, and the server serves it at `/download`, so
  `curl -fsSL http://localhost:5080/install.sh | sh` works. The server doesn't wait for it: until the
  build lands (several minutes of AOT compiling), `/download` answers 503 and `install.sh` exits
  with "pl is still being built".
- **Dex** for login, at <http://localhost:5556/dex>. The test user is `dev@example.com` with
  password `password`.
- **The compose deployer** (`compose/deployer`), the only service with the Docker socket. The
  self-deploy pipeline asks it to recreate the `pipelines` service with a new image
  ([docs/setup/compose-deploy.md](docs/setup/compose-deploy.md)).

```bash
docker compose up -d --build     # first run builds everything: several minutes
docker compose down -v           # stop and wipe all state (the reset between eval runs)
```

- **Auth is on by default and uses Dex.** `pl login` and the dashboard both log in through it.
  `pl login` uses the device flow when there is no local browser, as in WSL or a container.
  - To run without auth, set `PIPELINES_AUTH_DISABLE=true docker compose up -d`. Every endpoint is
    then anonymous.
  - A script can get a token without a browser through Dex's password grant:

    ```bash
    curl -s localhost:5556/dex/token -d grant_type=password -d client_id=olve-pipelines \
      -d username=dev@example.com -d password=password -d 'scope=openid profile email' | jq -r .access_token
    ```

    Pass the result as `PIPELINES_API_TOKEN`.
- **Pointing `pl` at the instance.** Give it its own profile, and keep `~/.pl` out of it with
  `PIPELINES_CONFIG`:

  ```bash
  export PIPELINES_CONFIG=/tmp/pl-local.json PIPELINES_PROFILE=local
  pl login --api-url http://localhost:5080        # Dex; device flow on a headless box
  pl binding create OliverVea/Olve.Pipelines --branch v2 --path .pipelines-test   # the v2 fixture
  pl binding reconcile <pipelineId>
  pl wait reconcile <pipelineId> <sha touching .pipelines-test or sdk>
  pl trigger <pipelineId> --field sha=<sha>
  pl wait run '<pipelineId>#1'
  ```

- **Requirements.**
  - Docker must allow privileged containers.
  - Ports 5080 and 5556 must be free. Set `PIPELINES_LOCAL_PORT` to move 5080.
  - Step pods need internet access for image pulls, GitHub tarballs and npm.
  - Bind a public repo, or set a token with `pl binding set-credentials`. Unauthenticated GitHub API
    calls are limited to 60 an hour.
  - Webhooks are never registered because `Webhooks:PublicBaseUrl` is unset. The deploy poll still
    works.
- **Rebuilding the harness image.** It is tagged `:local`, and k3s pulls it only if it isn't
  present yet. After changing `harness/base`, run `docker compose down -v` (or delete the
  `k3s-data` volume).
- **Windows checkouts.** `.gitattributes` keeps every file that ends up in a container LF. A
  checkout made before it existed still has CRLF files, so re-check them out once:

  ```bash
  git rm -r --cached -q . && git reset --hard   # discards uncommitted changes
  ```

- **Proving there is no homelab dependency.** `compose/isolation.yml` maps `registry.ovea.pro`,
  `auth.ovea.pro`, `otel.ovea.pro`, `hub-cache.ovea.pro` and `mcr-cache.ovea.pro` to 127.0.0.1 for
  the server, Dex and the k3s node:

  ```bash
  docker compose -f docker-compose.yml -f compose/isolation.yml up -d --build
  ```

- **Inspecting the cluster:** `docker compose exec k3s kubectl -n olve-runners get jobs,pods`.

#### Watch it deploy itself

The stack can run Olve.Pipelines' own deploy pipeline (`.pipelines/`), the one that deploys prod,
against itself: one instance that redeploys itself, with no beta. Only the config differs from prod
([compose/selfdeploy.config.yaml](compose/selfdeploy.config.yaml)):

- `target.beta` is `kind: none`, so the beta deploy and `beta-test` are left out.
- `target.prod` is `kind: compose`, so the pipeline's `select` picks `composeDeploy`: `deploy-prod`
  asks the `deployer` service to recreate the `pipelines` service (the instance running the
  pipeline) with the image the run built. The deployer waits for `/api/ready` and puts the old
  container back if the new one doesn't come up.
- Images go to the compose registry (`registry:5000`); `next` and `latest` are tags moved there.
  npm publishing is off, so `publish-sdk` is skipped and `promote` moves no dist-tag. No step talks
  to npm's registry for publishing, or to ghcr.io.
- `auth: none` and `telemetry: none` for the deployed instance.

```bash
export PIPELINES_CONFIG=/tmp/pl-local.json PIPELINES_PROFILE=local   # and log in, as above
pl binding create OliverVea/Olve.Pipelines --branch <a branch you pushed> --path .pipelines
pl binding status "Olve.Pipelines v2"        # reconciled; pending until it has config
printf local-registry | pl secret set "Olve.Pipelines v2" REGISTRY_PASSWORD
printf local-deployer | pl secret set "Olve.Pipelines v2" DEPLOYER_TOKEN
pl config set "Olve.Pipelines v2" -f compose/selfdeploy.config.yaml   # activates the version
pl trigger "Olve.Pipelines v2" --field sha=<the branch head>           # or push a commit
pl wait run "Olve.Pipelines v2#1" --until finished
curl -s http://localhost:5080/api/health      # image: the digest the run deployed
```

The run builds every component (the server image, `pl`, the SDK; minutes of AOT compiling and
several GB of memory in k3s), publishes the prereleases, deploys the instance and promotes. A first
run on a fresh stack also pulls the build images. The redeployed instance keeps its state: it is the
same container config and the same Garage, only the image changes.

- The deploy poll checks the branch every 15 minutes (`Reconcile:PollIntervalSeconds`), and webhooks
  are off in the stack: after a push, `pl binding reconcile` picks it up at once.
- The run survives the redeploy: the new server reattaches to the run's Jobs, as on prod.
- `docker compose up` names `olve-pipelines-v2:local` again, so it can put the locally built image
  back over a deployed one.

**The in-cluster variant** ([compose/selfdeploy-incluster.config.yaml](compose/selfdeploy-incluster.config.yaml))
tests the helm chart instead: beta and prod are the namespaces `selfdeploy-beta` and
`selfdeploy-prod` of the compose k3s, both `kind: inCluster`, so the pipeline's `select` picks
`helmDeployInCluster`. It runs helm in its own pod as the step ServiceAccount `olve-deployer`
(`Kubernetes__StepServiceAccount`), which the bootstrap makes admin of the two `selfdeploy-*`
namespaces and nothing else (`compose/k3s-selfdeploy.yaml`). Set it with
`pl config set "Olve.Pipelines v2" -f compose/selfdeploy-incluster.config.yaml` (only
`REGISTRY_PASSWORD` is needed). The run also deploys beta and smoke-tests it.

- Each deployed copy has its own Garage and runs its own step Jobs in its namespace, with the
  harness image the run built. On the first install the server restarts a few times until its new
  Garage answers; that is the readiness gate working, not a failure.
- Look at a copy with `docker compose exec k3s kubectl -n selfdeploy-prod get pods`. To drive one,
  run `kubectl -n selfdeploy-beta port-forward --address 0.0.0.0 svc/olve-pipelines 18081:80` inside
  the k3s container, then point `pl` (auth is off) at `http://k3s:18081` from a container on the
  compose network.
