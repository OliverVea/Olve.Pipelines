# CLAUDE.md

See [README.md](README.md) for project structure, endpoints, configuration, CI examples, and client generation.

Beads label for this project: `project:olve-pipelines` (kebab-case, not `Olve.Pipelines`). Use `bd ready --label project:olve-pipelines` to find ready work.

## Design

Olve.Pipelines is a lightweight CD pipeline configuration and orchestration service for a homelab.

### Pipeline Flow

A pipeline has two phases:

```
Production [N steps, parallel] ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N [sequential]
```

Each production step runs in parallel and writes output to `bundle/<step-name>/`. The combined output is an **ArtifactBundle**. Processing steps run sequentially, each receiving the full ArtifactBundle.

### Entities

- **Pipeline** — top-level entity grouping all configuration for a single CD workflow.
- **ProductionStep** — a parallel build/source step. A pipeline has many production steps. Each is configured with `(image, script, env)` via a **StepConfiguration** attachment. Combined output of all production steps is an **ArtifactBundle**.
- **ProcessingStep** — a sequential post-build action (e.g. deploy to staging). A pipeline has an ordered list of processing steps. Each is configured with `(image, script, env)` via a **StepConfiguration** attachment.
- **Trigger** — a named trigger attached to a pipeline. Three target types:
  - `ProductionTriggerTarget` — fires all production steps (used by webhooks).
  - `ProcessingTriggerTarget(ProcessingStepId)` — fires a specific processing step with an artifact bundle.
  - `PollTriggerTarget(Url, Headers, ValuePath, IntervalSeconds)` — background poller that GETs a URL, extracts a JSON value via dot-path, and triggers production when the value changes. Header values can reference pipeline K8s secrets via `$SECRET:NAME`.
- **ArtifactBundle** — the collected outputs as a zipped directory in S3: `bundle/<step-name>/<files>`. Produced by production, consumed by processing steps.
- **Job** — a scheduled unit of work. Two types: `ProductionJob` and `ProcessingJob`.

### Step Configuration

Every step (production or processing) shares the same configuration shape: `StepConfiguration(Image, Script, EnvironmentVariables)`. Configuration is attached via `AttachmentStore<TStep, StepConfiguration>` (composition, not inheritance). Future typed templates (e.g. DotNetBuild, HelmDeploy) will be additional attachment types that pre-populate image/script/env.

### Configuration vs Execution

Configuration defines *what* each step does. Execution is triggered via `POST /api/pipelines/{id}/trigger/production`. Processing step triggering will propagate the artifact bundle through the pipeline automatically.

### Execution

All pipeline steps execute as **Kubernetes Jobs**. Each Job gets:
- A container image
- A script to run
- Environment variables from step configuration
- Pipeline secrets (stored as K8s Secrets, auto-mounted)
- Input/output bundle references (S3 keys)

### Job Scheduling

Jobs are first-class persisted entities managed by a **JobQueue**. The queue controls when and how jobs are submitted to Kubernetes.

**Job types:**
- `ProductionJob` — runs all production steps, produces an ArtifactBundle
- `ProcessingJob` — runs a single processing step with a given ArtifactBundle

**Job statuses:** `Scheduled`, `InProgress`, `Done`, `Obsolete`, `Cancelled`

**Scheduling rules:**
- **Keyed on (pipeline, step)** — each step can have at most one `InProgress` job at a time.
- **Latest-wins** — when a new job is scheduled for a key that already has `Scheduled` jobs, those become `Obsolete`. Only the newest scheduled job will run.
- **Cascade on pipeline delete** — all scheduled/in-progress jobs are cancelled.

### Pipeline Secrets

Each pipeline has a K8s Secret (e.g. `olve-pipeline-{id}`) containing credentials needed by its steps (registry tokens, deploy keys, API keys). Secrets are managed via the API but injected directly from K8s into Jobs — they never pass through the app at runtime.

## Commands

```bash
dotnet build                                                # Build
dotnet test                                                 # Unit tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false  # Integration tests only
dotnet test -p:RunIntegrationTests=true                     # All tests
dotnet run --project src/Olve.Pipelines                     # Run locally
```

### Frontend

See [frontend/README.md](frontend/README.md) for full details.

```bash
cd frontend && npm install                                  # Install
cd frontend && npm run dev                                  # Dev server (proxies /api to localhost:5000)
cd frontend && npm run dev:prod                             # Dev server (proxies /api to https://pipelines-private.ovea.pro)
cd frontend && npm run build                                # Production build
```

- Stack: Lit + Vite + TypeScript
- API client: Kiota-generated at `clients/olve-pipelines-client-ts/`, linked via `file:` reference
- Regenerate client after API changes: `kiota generate -l typescript -d api.json -o clients/olve-pipelines-client-ts/src -n OlvePipelinesClient --clean-output`
- Client deps must be installed separately: `cd clients/olve-pipelines-client-ts && npm install`

### Architecture Patterns

**Service layers (per domain area):**
- **EntityStore\<T\>** — generic in-memory CRUD with ConcurrentDictionary (singleton, holds state, fires `Event<Id<T>>` on mutations)
- **AttachmentStore\<TParent, TAttachment\>** — parallel store for optional attachments, auto-cleanup on parent deletion (singleton)
- **Domain event hubs** (e.g. `JobEvents`) — hold `Event<T>` properties, receive forwarded store events (singleton)
- **Domain CRUD services** (e.g. `JobService`) — typed create/read/update/delete over the store, no event awareness (transient)
- **Domain rule services** (e.g. `JobObsoletionService`) — implement business rules, event-driven (transient)
- **Domain query services** (e.g. `JobQueueService`) — stateless queries over the store (transient)
- **Cleanup services** (e.g. `ProductionStepCleanupService`) — cascade deletes on parent deletion, event-driven (transient)

**Key principles:**
- **Sync core logic** — all domain services are synchronous. Async is reserved for I/O boundaries (K8s client, S3, HTTP).
- **Two-tier events** — EntityStore fires low-level CRUD events, forwarded to domain event hubs. Subscribers subscribe to domain hubs, not the store.
- **Explicit event registration** — each domain area has a `*EventRegistration` class implementing `IRunOnStartup`. Each line is one subscription. Handlers are resolved from `IServiceProvider` at dispatch time so services stay transient.
- **Transient by default, singleton only for state** — don't cache what you can query. Keep derived state as queries until performance proves otherwise.
- **Result error handling** — no exceptions, `Olve.Results` everywhere.
- **IRunOnStartup over IHostedService** — domain startup wiring uses `IRunOnStartup` (sync, returns `Result`). A single `StartupRunner` hosted service runs them all. Keeps `IHostedService` out of domain code.
- **Seams for testing** — `IdProvider` (virtual), `TimeProvider` (abstract). Wire store -> events -> subscribers explicitly in tests, no DI container needed.
- **Named endpoints** — all endpoints use `.WithName()` to set `operationId` in the OpenAPI spec, giving clean generated client method names.
- **Split routes for nested resources** — pipeline-scoped operations (create, list) use `/api/pipelines/{pipelineId}/...`, step-scoped operations (get, delete, config) use `/api/production-steps/{stepId}/...` to avoid Refit parameter mismatch.

## Conventions

- .NET 10, C# with file-scoped namespaces, nullable enabled, implicit usings
- Package versions managed centrally in `Directory.Packages.props` — do not add `Version` attributes in csproj files
- Local config via `dotnet user-secrets`, not appsettings files
- OpenAPI spec `api.json` is generated on build by `Microsoft.Extensions.ApiDescription.Server`
- AOT publishing enabled — do not add AOT-incompatible libraries (e.g. EF Core)
- Namespaces are always plural/more general than contained types (e.g. `Pipeline` type in `Pipelines` namespace)
- Domain subnamespaces nest under their parent (e.g. `Pipelines.Production`, `Pipelines.Processing`, `Pipelines.Building`)
- Storage via S3-compatible MinIO (minio.ovea.pro) — JSON files for persistence, zipped directories for bundles
- TypeScript client generated from OpenAPI via Kiota
- C# client generated from OpenAPI via Refitter with `returnIApiResponse: true` (no exceptions on error status codes)

## References

- [Olve.* packages](https://olivervea.github.io/Olve.Utilities/) — index of all Olve packages
  - [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — non-throwing result types for error handling
  - [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — input validation built on Olve.Results
  - [Olve.MinimalApi](https://olivervea.github.io/Olve.Utilities/src/Olve.MinimalApi/README.html) — result-to-HTTP mapping for minimal APIs
  - [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — identifiers, collections, graph types
  - [Olve.Results.TUnit](https://olivervea.github.io/Olve.Utilities/src/Olve.Results.TUnit/README.html) — TUnit assertions for Result types (`Succeeded()`, `Failed()`, etc.)
- [TUnit](https://tunit.dev/docs/intro) — test framework, uses `await Assert.That(...)` fluent syntax (not xUnit/NUnit)
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — source-generated mocking (AOT-compatible)
- [Refitter](https://refitter.github.io/articles/refitter-file-format.html) — C# client source gen from OpenAPI via Refit (.refitter file format)
- [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview) — TypeScript client gen from OpenAPI
