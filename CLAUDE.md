# CLAUDE.md

See [CONTRIBUTING.md](CONTRIBUTING.md) for project structure, build and client/API-reference generation; server configuration is in [docs/setup/self-hosting.md](docs/setup/self-hosting.md), the `pl` CLI in [docs/setup/cli.md](docs/setup/cli.md), the API in [docs/setup/api-reference.md](docs/setup/api-reference.md) (generated from `api.json`). The v2 spec is [docs/design/pipelines-v2.md](docs/design/pipelines-v2.md); what was actually built, per track, with decisions, deviations and known gaps, is [docs/design/pipelines-v2-plan.md](docs/design/pipelines-v2-plan.md). User-facing docs (served at `/docs`) are in [docs/setup](docs/setup).

## Start here: load the agent playbook

**Before running any `pl` command, deploying, or touching a `.pipelines/` directory, load the `ovea-olve-pipelines` skill** (`ovea-workflow:ovea-olve-pipelines`). It is how this project expects agents to drive Olve.Pipelines: `pl` for everything, the user logs in, profiles, `pl wait` instead of poll loops, what a push does, changing a pipeline safely. This repo's `.claude/settings.json` enables its plugin (`ovea-workflow` from the `ovea-skills` marketplace, [OliverVea/ovea-skills](https://github.com/OliverVea/ovea-skills)). If the skill isn't listed, ask the user to install or update the plugin (`/plugin`); until then read [the SKILL.md](https://github.com/OliverVea/ovea-skills/blob/master/ovea-workflow/skills/ovea-olve-pipelines/SKILL.md) directly.

## Design

Olve.Pipelines is a small GitOps CD orchestration service for a homelab. This is **v2** (typed pipelines in TypeScript); it is fully breaking — v1's production/processing steps, ArtifactBundles, job groups, `config.yaml` and `StepConfiguration` are gone, not adapted.

### Pipeline model

A pipeline lives in the repo it deploys, in the binding path (`.pipelines/` by default), written against the SDK `@olivervea/pipelines` (`src/sdk/`):

- **Model** — a named, nominal zod schema (`model("BuildArtifact", z.object(…))`), emitted as JSON Schema with an `input` view (`z.input`, defaults optional) and a strict `output` view. Files are references (`fileRef()` → `{run, step, attempt, path, sha256}`), never inline.
- **Step** — a contract: name (the identity — history, scheduling, blocked steps and addresses are keyed on it), image (any x86-64 image), inputs (model, `optional(model)` or plain schema), output model, declared secrets, `timeoutSeconds`. No behaviour.
- **Handler** — a top-level `handlers/*.ts` default-exporting `implement(step, (input, ctx) => output)`; the `step` reference pairs them (file names are free, no convention). **Library steps** (`gitHubSource`, `imageBuild`, `helmDeployOverSsh`, `helmDeployInCluster`, `composeDeploy`) and other **reusable steps** are functions returning `implement(step({ kind, settings, … }), handler)`, staged directly; the library uses the public API only (`library-public.test.ts`), so a step package outside the SDK works the same. The build hashes the modules that call `step`/`implement` per kind (`kindHashes`). To the service a kind is only a label, so a new library or package step is SDK-only. The library grows only from steps several pipelines use; single-use steps stay custom in their pipeline.
- **Pipeline** — input model, triggers, ordered **stages** (steps in a stage run in parallel; a stage is a barrier), nested **`when`** blocks (declarative matches on literal-like input fields), and `onFailure` handlers. Inputs are matched by model, or wired with a `[step, mapper]` tuple; mappers are recorded as tagged mappings (`{ref}`/`{const}`), so the service builds `input.json` without running TypeScript.
- **Triggers** — `onPush` (the binding saw the branch head move: webhook or deploy poll; the service sends `{ref, after, repository: {full_name}}`), `onPoll` (GET a URL, extract a value by dot-path, per-trigger secrets with `$SECRET:NAME` headers), manual (`POST …/runs`, input validated against the input model). Automatic trigger mappers are code: each event runs a short `map-trigger` Job, then the service re-validates the result.

The SDK compiles the pipeline into **one executable** (`bun build --compile`) with three modes: `describe` (→ the canonical **manifest**, `src/sdk/manifest.schema.json`), `run <step>`, `map-trigger <trigger>`. **The service never executes repo TypeScript** — describe, handlers and mappers run only in sandboxed Jobs.

### Reconcile, versions, switch

- `DeployPollService` syncs each binding's config, **config-before-run**: the newest commit touching the binding path is reconciled first (`PipelineReconcileService`: build/describe Job `olve-build-<p12>-<sha12>` → manifest from the pod log sentinels → `ManifestParser` + `ManifestValidator` (every reconcile check) → `ManifestVersioning` (entity versions) → a new `PipelineVersion` if the manifest differs → `SwitchService.Switch`). Every automatic run start of a bound pipeline goes through its lock (`IConfigLock`) and starts only once the current config commit is applied: push (`PushTriggerService`: a poll source per bound pipeline that declares `onPush`, nudged by the binding's GitHub webhook; a moved branch head past `LastPushSha` fires the push trigger), poll and webhook triggers. A failed reconcile is not retried by the loop (only by reconcile-now or restart). The config cursor (`ConfigCursor`) watches the binding path plus the local `file:`/`link:` dependencies of its `package.json` (e.g. `../src/sdk`); only commits touching those build a binary.
- **Versions** are immutable: pipeline version = monotonic integer per pipeline (manifest + commit + binary key); steps and models have `<id>.<version>` bumped only when that entity's content changes (a step's hash includes its handler bundle hash).
- **Single active version.** `PipelineActivation` is `Active(v)` (new runs start there) or, only during a `--cancel-running` switch, `Draining(from → to)`. A normal switch completes at once: runs on older versions carry on with the version they pinned. `StageScheduler` never lets a run start a step a newer run already started (it is superseded instead), so an old-version run can't deploy after a newer one. `--cancel-running` cancels the other versions' runs and in-flight attempts; requests meanwhile queue (`PendingRunStart`) and start on the new version. Rollback = `switch` to an older version.

### Runs, attempts, scheduling

- A **Run** pins one version and stores its input; its attempts' outputs form the pool. Numbered per pipeline; version stamp `yyyy.MM.dd.<number>`. Statuses: Running, Succeeded, Failed, Cancelled, Superseded. Addressed as `<pipeline>#<run>/<step>[@<attempt>]` (no attempt = latest successful attempt).
- An **Attempt** is one execution of one step = one K8s Job. Statuses: Scheduled, InProgress, Succeeded, Failed, Skipped (scheduler skip with chained reason, or a handler `ctx.skip`), Cancelled, Obsolete. A step whose `when` doesn't match the run input is **excluded**: not part of the run, no attempt (the run view says `excluded`; step history leaves the run out).
- `StageScheduler.Advance` is idempotent and stateless (re-derives everything from the stores): for the first unfinished stage, per step — `when` (excluded: no attempt) → input availability (skip propagation) → blocked (held) → schedule with `input.json` built by `AttemptInputBuilder` from the tagged mappings over the pool. A failed step fails the run at the barrier. Scheduling mutations run under `RunSchedulingLock`.
- **Keyed on step name, not version**: `JobRunner` allows one InProgress attempt per (pipeline, step); `JobObsoletionService` makes older Scheduled attempts of the same key Obsolete (latest-wins) and supersedes their run.
- `RunStartService`: runs flow on concurrently: starting one leaves older runs running (they meet only per step, under the two rules above, so under a stream of pushes an older run still reaches prod); **de-dup** — automatic triggers only, keyed on the canonical hash of the mapped input, against a Running run or the latest automatic run within 1 h on the same version. Explicit actions (manual trigger, rerun, continue) always create work.
- `RunContinuationService`: **rerun step** (a new attempt, same pinned version + pool; *rerun only* or *rerun and continue*) and **continue** (merged input validated against the pinned model; fields consumed by completed steps can't change). Both need the run's version to be active. `RunCancellationService`: explicit cancel also cancels in-progress attempts (Jobs deleted). Automatic retries only when a step opts in with `retries: n` (`StageScheduler`: a failed attempt is retried while the trailing failure streak since the run was last resumed is ≤ n; cancelled/obsolete attempts and scheduler-side failures never).
- **Blocked steps** (`StepBlockService`, `PipelineBlockedSteps` attachment on the pipeline, persisted in `pipeline-state.json`): an operator hold for working on a pipeline or an incident, not an approval mechanism ("gate" is reserved for gating steps in a pipeline: tests, approval, visual regression). Operational state, not config — keyed on step name, survives switches and reconcile. A blocked step gets no attempt and holds its run (`Run.HeldAt`); unblocking resumes held runs; an explicit step rerun of a blocked step is refused. Promote = continue / rerun-and-continue.

### Execution (harness)

`AttemptLaunchService` derives everything for one attempt; `HarnessJobFactory`/`HarnessJobBuilder` build the K8s Job: one init container (`harness-base`: static busybox + a bundled glibc loader under `/olve/lib`) fetches the version's `step.tar.gz` and the attempt's `input.json` via pre-signed GETs into the `/olve` emptyDir; the step container (the step's image) runs `/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step run <step>` with `TMPDIR=/olve/tmp`, only the step's declared secrets (`secretKeyRef`, same names, optional: unset = env var unset), `activeDeadlineSeconds` from `timeoutSeconds`, runtime class / labels. The harness PUTs `result.tar.gz` (envelope `output.json` + files); `HarnessResultCollector` streams it into `runs/<p>/<run>/<step>/<attempt>/` (memory-safe for big files; output JSON capped at 64 KB). Exit codes and the envelope are specified in `src/sdk/README.md`. After a restart the executor reattaches to Jobs by name (v2 survives redeploying itself mid-run).

**Failure handlers**: a run going Running → Failed makes `FailureHandlerService` launch each in-scope `onFailure` handler of the *pinned* manifest as an untracked, best-effort harness Job (`olve-fh-…`, role `on-failure`) with a typed `FailureContext` (validated against the pinned schema first; links from `Links:BaseUrl`). No attempt, no retry, no API/UI visibility.

**Retention** (`RetentionService`, `Retention:*`, **dry run by default**): a run goes only when beyond the latest 20 **and** older than 183 days; binaries are kept for active/draining versions and retained runs' versions; leftovers and leaked URL Secrets / Jobs are swept. A pipeline overrides the three thresholds with `retention` in its definition (manifest root; `RetentionPolicies` reads the active version's).

### Pipeline secrets

Each pipeline has a K8s Secret `olve-pipeline-{id:N}` in the runner namespace. Values are set via the API (`pl secret set`) and injected from K8s into Jobs — a step gets only the secrets it declares; they never pass through the app at runtime.

### Persistence

State survives restarts (notably v2's own self-deploy). Garage objects: `configuration.json` (pipelines + bindings), `binding-hooks.json`, and from `RunPersistenceService` `pipeline-versions.json`, `versions/<p>/<n>/manifest.json`, `pipeline-state.json` (activations, sequences, blocked steps, queued run starts, poll values, pending versions), `runs.json`, `runs/<p>/<n>/run.json` (a run + all its attempts), `pipeline-config.json` + `config/<p>/<n>.yaml` (config snapshots, write-once). Binaries: `versions/<p>/builds/<sha>/step.tar.gz`. Layout helpers: `StorageLayout`. `/api/ready` reports when persisted state is loaded.

### Service map

| Area | Key types |
|---|---|
| `Pipelines` | `Pipeline`, `PipelineService`, `PipelineSummaryService`, `PipelineHealthService`, `StorageLayout` |
| `Pipelines.Sync` | `PipelineConfigBinding(Service)`, `DeployPollService`, `PipelineReconcileService`, `BindingWebhookReceiver`, `BindingHook*`, `GitHubConfigSource` |
| `Pipelines.Manifests` | `Manifest`, `ManifestParser`, `ManifestValidator`, `ManifestVersioning`, `JsonSchemaValidator`, `CanonicalJson` |
| `Pipelines.Versions` | `PipelineVersion(Service)`, `PipelineActivationService` |
| `Pipelines.Configs` | `ConfigSnapshot(Service)`, `ConfigService`, `ConfigValidation`, `ConfigActivationService` (the activation gate, `PendingVersion`), `YamlJson` |
| `Pipelines.Triggers` | `PushTriggerService`, `PushTriggerHandler`, `PollTriggerService`, `WebhookTriggerService`, `TriggerMappingService`, `RunInputService` |
| `Pipelines.Scheduling` | `RunStartService`, `StageScheduler`, `RunPlan`, `AttemptInputBuilder`, `RunContinuationService`, `RunCancellationService`, `StepBlockService`, `SwitchService`, `AttemptLaunchService` |
| `Pipelines.Runs` | `Run`, `Attempt`, `RunService`, `AttemptService`, `RunQueryService`, `RunViewService`, `CriticalPathService`, `AttemptFileService`, `RunCleanupService` |
| `Pipelines.Failures` / `Pipelines.Retention` | `FailureHandlerService`, `FailureContextBuilder` / `RetentionService`, `RetentionPlanner` |
| `Distribution` | `CliDownloadEndpoints` (`/download/{asset}`, `/install.sh`), `CliReleaseQuery` (latest `publish-cli` attempt of `Distribution:CliPipeline`; retention pins its run) |
| `Jobs` | `JobRunner`, `JobObsoletionService`, `JobQueueService`, `KubernetesJobExecutor`, `NoOpJobExecutor` (tests), `JobLogService` |
| `Kubernetes.Harness` | `HarnessJobFactory`, `HarnessJobBuilder`, `BuildJobBuilder`, `HarnessResultCollector`, `HarnessFailureHandlerLauncher`, `HarnessJobControl` |
| `Streaming` | `ChangeBroadcaster` (SSE fan-out, `/api/events`), `ChangeEventRegistration` (domain hubs → notices), `AttemptLogStreamer` |
| `Shared.Persistence` | `ConfigurationPersistenceService`, `RunPersistenceService`, `IPersistenceReadiness` |

When adding a new way to start work, go through `RunStartService` (runs) or `RunContinuationService` (attempts in an existing run) so latest-wins, de-dup, draining and blocked steps apply; never create attempts directly.


## Commands

```bash
dotnet build                                                # Build (compiles src/spec/ first; needs bun)
dotnet test                                                 # Tests
dotnet run --project src/backend/Olve.Pipelines                     # Run locally
cd src/spec && bun run build                                # The API contract alone → artifacts/ (OpenAPI, generated C#, TS client; see docs/design/spec-first.md)
tools/check.sh [--changed|--full|--fix]                     # What the deploy checks, locally; --fix formats and regenerates (see CONTRIBUTING.md)
```

A `PreToolUse` hook runs `tools/check.sh --hook` before every `git push`: a failing check blocks the push. Fix with `tools/check.sh --fix` (formatting, generated files) or by hand, never by bypassing the hook.

### SDK and pipelines (Bun)

See [src/sdk/README.md](src/sdk/README.md) (authoring API, harness contract, manifest, `olve-build`, `olve-dev`) and [src/harness/README.md](src/harness/README.md) (the `harness-base` init image).

```bash
cd src/sdk && bun install                                       # Install
cd src/sdk && bunx tsc --noEmit -p .                            # Type-check (TS 7)
cd src/sdk && bun run check                                     # prettier, tsc, compile-fail and runtime tests (what build-sdk runs)
cd src/sdk && bun scripts/gen-manifest-schema.ts                # After changing src/describe/manifest-schema.ts
pl dev describe [--dir .pipelines-test]                     # Describe a pipeline from source (see docs/setup/local-development.md)
```

Keep the compile-fail checker green on both TypeScript lines (TS 7 and 5.9; `TSC=… bun scripts/check-compile-fail.ts`).

### Frontend

See [src/frontend/README.md](src/frontend/README.md) for full details.

```bash
cd src/frontend && npm install                                  # Install
cd src/frontend && npm run dev                                  # Dev server (proxies /api to localhost:5000)
cd src/frontend && npm run dev:prod                             # Dev server (proxies /api to https://pipelines-private.ovea.pro)
cd src/frontend && VITE_DEV_AUTH=bypass npm run dev:v2beta      # Dev server against v2 beta (dev-only auth bypass)
cd src/frontend && npm run build                                # Production build
```

- Stack: Lit + Vite + TypeScript
- API client: Kiota-generated at `src/clients/olve-pipelines-client-ts/`, linked via `file:` reference
- Regenerate client after API changes: `dotnet tool restore && dotnet kiota generate -l typescript -d api.json -o src/clients/olve-pipelines-client-ts/src -n OlvePipelinesClient --clean-output` (Kiota pinned in `.config/dotnet-tools.json`; >= 1.36 needed — 1.30 emits a `tsc` error on nullable polymorphic members)
- Client deps must be installed separately: `cd src/clients/olve-pipelines-client-ts && npm install`

### Architecture Patterns

**Service layers (per domain area):**
- **EntityStore\<T\>** (from `Olve.Utilities.Stores`) — generic in-memory CRUD with ConcurrentDictionary (singleton, holds state, fires `OnAdded`/`OnUpdated`/`OnDeleted` after each write, carrying the committed entity: `EntityAdded`, `EntityUpdated` (`Before`/`After`), `EntityDeleted`)
- **AttachmentStore\<TParent, TAttachment\>** — parallel store for optional attachments, auto-cleanup on parent deletion (singleton)
- **Domain event hubs** (e.g. `RunEvents`) — hold `Event<T>` properties, receive forwarded store events (singleton)
- **Domain CRUD services** (e.g. `PipelineService`) — typed create/read/update/delete over the store, no event awareness (transient; `RunService`, `AttemptService` and `PipelineVersionService` are singletons because they own lookup indexes and the lock that keeps numbers unique)
- **Domain rule services** (e.g. `JobObsoletionService`) — implement business rules, event-driven (transient)
- **Domain query services** (e.g. `JobQueueService`, `RunViewService`) — stateless queries over the store (transient)
- **Cleanup services** (e.g. `RunCleanupService`, `PipelineVersionCleanupService`) — cascade deletes on parent deletion, event-driven (transient)

**Key principles:**
- **Sync core logic** — all domain services are synchronous. Async is reserved for I/O boundaries (K8s client, S3, HTTP).
- **Two-tier events** — EntityStore fires low-level CRUD events, forwarded to domain event hubs. Subscribers subscribe to domain hubs, not the store.
- **Explicit event registration** — each domain area has a `*EventRegistration` class implementing `IRunOnStartup`. Each line is one subscription. Handlers are resolved from `IServiceProvider` at dispatch time so services stay transient.
- **Transient by default, singleton only for state** — don't cache what you can query. Keep derived state as queries until performance proves otherwise.
- **Result error handling** — no exceptions, `Olve.Results` everywhere. The problem type decides the HTTP status (`NotFoundProblem` 404, `ConflictProblem` 409, `ValidationProblem` 400 with `Field`, `UnavailableProblem` 503; `Shared/Problems`); endpoints use `WithApiResultMapping`, which answers an `ApiProblem` body (see the error-model entry in the v2 plan).
- **IRunOnStartup over IHostedService** — domain startup wiring uses `IRunOnStartup` (sync, returns `Result`). A single `StartupRunner` hosted service runs them all. Keeps `IHostedService` out of domain code.
- **Seams for testing** — `IdProvider` (virtual), `TimeProvider` (abstract). Wire store -> events -> subscribers explicitly in tests, no DI container needed.
- **Named endpoints** — all endpoints use `.WithName()` to set `operationId` in the OpenAPI spec, giving clean generated client method names.
- **Routes** — pipeline-scoped resources live under `/api/pipelines/{pipelineId}/...`, addressed the way users address them: runs by number, steps by name (`/runs/{number}/steps/{step}/...`, `/steps/{step}/block`). Only cross-pipeline views are top-level (`/api/attempts?status=active`).

## Conventions

- .NET 10, C# with file-scoped namespaces, nullable enabled, implicit usings
- Package versions managed centrally in `Directory.Packages.props` — do not add `Version` attributes in csproj files
- Local config via `dotnet user-secrets`, not appsettings files
- OpenAPI spec `api.json` is generated on build by `Microsoft.Extensions.ApiDescription.Server`
- AOT publishing enabled — do not add AOT-incompatible libraries (e.g. EF Core)
- Namespaces are always plural/more general than contained types (e.g. `Pipeline` type in `Pipelines` namespace)
- Domain subnamespaces nest under their parent (e.g. `Pipelines.Runs`, `Pipelines.Scheduling`, `Pipelines.Versions`)
- Storage via a per-instance, in-cluster S3-compatible Garage store (replaced MinIO 2026-09) — JSON files for persistence, step outputs/files/logs under `runs/<p>/<run>/<step>/<attempt>/`, binaries and manifests under `versions/` (see `StorageLayout`). Step pods only ever get pre-signed URLs, signed for `Storage:InClusterEndpoint`
- New C# types that cross JSON (API, persistence, harness) are registered in the source-gen JSON contexts (AOT)
- Commit the rebuilt `api.json`, regenerate the TypeScript client (`api.json` → Kiota) and the API reference (`dotnet run tools/api-reference.cs`) after any API change: the deploy's `test` step fails when any is out of date. Every endpoint has `.WithTags`, `.WithSummary` (and `.WithDescription` when it needs more); that text is the API reference
- TypeScript client generated from OpenAPI via Kiota
- The `pl` CLI has a hand-rolled AOT-safe client in `src/backend/Olve.Pipelines.Cli/Api/` (add each endpoint there by hand).
- **Logging** — structured `ILogger<T>` with named placeholders (never string interpolation); always log human-readable names alongside UUIDs; log `ResultProblem` failures via `ILogger.LogProblems(...)` (flattens to `ProblemCount`/`ProblemMessages`/`MaxSeverity`), never a `{Problems}` blob. Backend-agnostic: stdout only, JSON formatter selected by config (`Logging__Console__FormatterName=json` in helm), no log endpoint/token in the repo. See [docs/setup/self-hosting.md](docs/setup/self-hosting.md#logging).
- Commit work and push directly to `main`. No feature branches or PRs unless the user asks; commit straight onto `main` and `git push`.
- **Tests assert results, never read `.Value!`**: `await Assert.That(result).Succeeded()` / `.Failed()` / `.SucceededAndValue(v => v.IsEqualTo(x))` (Olve.Results.TUnit); to unwrap a result for setup, `TestRunDomain.Pick(result)` (throws with the problems on failure).
- **Agent playbook: keep the `ovea-olve-pipelines` skill current.** It lives in [OliverVea/ovea-skills](https://github.com/OliverVea/ovea-skills) (`ovea-workflow/skills/ovea-olve-pipelines/SKILL.md`) and tells agents how to use Olve.Pipelines: `pl` for everything, the user logs in, profiles, `pl wait`, what a push does, changing a pipeline safely. Any change here that alters how an agent should use the service, `pl` or the SDK updates that skill in the same piece of work, and bumps the `ovea-workflow` plugin version (clients update on a version bump).
- **User docs (`docs/setup`) describe the current system only**: no v1, no history, no "replaced by"/"no longer". Keep them up to date with every change; migration notes and history go in `docs/design`.
- **Comments: few and short.** Comment only what the code can't say (a non-obvious why, a gotcha). No narrative headers, no restating the code, no plan/track/spec-section references or history in comments; that belongs in docs and commit messages. Pipeline definitions (`.pipelines/`) and SDK examples especially must read cleanly.
- Development happens on `main`. The deployer pipeline (`.pipelines/pipeline.ts`) lives on v2 prod (`pipelines-private.ovea.pro`): every push deploys v2 beta, smoke-tests it (`beta-test`), then deploys v2 prod; `deployTo=beta` stops after beta. Block `deploy-prod` (`pl step block`) only in special circumstances.
- **Server first, then the pipeline that needs it.** Prod reconciles `.pipelines/` with the server code it is running, so a commit whose pipeline needs a new server rule (a validator check, a manifest field) fails reconcile and the deploy stalls. Push the server change on its own, let it reach prod, then push the pipeline change.
- When reporting a pushed change to the user, give the run that deploys it (`pl wait push "Olve.Pipelines v2" <sha>` → `Olve.Pipelines v2#<n>`) and its state.

## References

- [Zod](https://zod.dev) (v4) — model schemas in the SDK; JSON Schema via `z.toJSONSchema`
- [Bun](https://bun.sh/docs) — SDK runtime, test runner and `bun build --compile` for the step binary
- [Olve.* packages](https://olivervea.github.io/Olve.Utilities/) — index of all Olve packages
  - [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — non-throwing result types for error handling
  - [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — input validation built on Olve.Results
  - [Olve.MinimalApi](https://olivervea.github.io/Olve.Utilities/src/Olve.MinimalApi/README.html) — result-to-HTTP mapping for minimal APIs
  - [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — identifiers, collections, graph types
  - [Olve.Results.TUnit](https://olivervea.github.io/Olve.Utilities/src/Olve.Results.TUnit/README.html) — TUnit assertions for Result types (`Succeeded()`, `Failed()`, etc.)
- [TUnit](https://tunit.dev/docs/intro) — test framework, uses `await Assert.That(...)` fluent syntax (not xUnit/NUnit)
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — source-generated mocking (AOT-compatible)
- [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview) — TypeScript client gen from OpenAPI
