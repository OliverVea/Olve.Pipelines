# Contributing

How the codebase is laid out and built. Using Olve.Pipelines: [docs/setup](docs/setup/index.md).
Design: [docs/design/pipelines-v2.md](docs/design/pipelines-v2.md) (spec),
[docs/design/pipelines-v2-plan.md](docs/design/pipelines-v2-plan.md) (what was built), and
[docs/design/roadmap.md](docs/design/roadmap.md). Agents: [CLAUDE.md](CLAUDE.md), [AGENTS.md](AGENTS.md).

## Project structure

```
src/Olve.Pipelines/                 API + scheduler (minimal API, Native AOT)
├── Configuration/                  Auth, telemetry, JSON, host, storage, DI wiring
├── Distribution/                   /download/{asset} (latest publish-cli files), /install.sh|.ps1
├── GitHub/                         GitHub client, push webhook payload + signature
├── Health/                         /api/health, /api/ready
├── Jobs/                           JobRunner, K8s executor, obsoletion, job (attempt) endpoints
├── Kubernetes/                     K8s client, pipeline Secrets, in-cluster credentials
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
tools/api-reference.cs              Generates docs/setup/api-reference.md from api.json
```

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

## Logging conventions

Structured `ILogger<T>` with named placeholders; log human-readable names next to ids. Failures
are logged with `ILogger.LogProblems(...)` (`src/Olve.Pipelines/Shared/ResultProblemLogExtensions.cs`),
which flattens a `ResultProblem` set into `ProblemCount` / `ProblemMessages` / `MaxSeverity`.
What an operator sees: [docs/setup/self-hosting.md](docs/setup/self-hosting.md#logging).

## Clients and the API reference

`api.json` is regenerated on every `dotnet build`. After an API change, regenerate the TypeScript
client and the API reference, and commit both: the deploy's `test` step fails when either is out of
date.

### API reference

```bash
dotnet run tools/api-reference.cs          # docs/setup/api-reference.md from api.json + tools/api-reference.header.md
```

Endpoint text comes from the code: `.WithTags`, `.WithSummary` and `.WithDescription` on each
endpoint. Auth, errors, live events and the routes outside the API are hand-written in the header.

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
