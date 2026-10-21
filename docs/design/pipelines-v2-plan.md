# Olve.Pipelines v2 — implementation plan

Companion to [pipelines-v2.md](pipelines-v2.md) (the spec) and
[pipelines-v2-spikes.md](pipelines-v2-spikes.md) (spike findings). Work happens on the `v2` branch;
v2 beta (`pipelines-v2-beta.ovea.pro`) is deployed by the v1 pipeline "Olve.Pipelines v2" from this
branch's `.pipelines/config.yaml` until milestone M4.

## Ground rules

- **Fully breaking.** v1 elements that conflict with the spec are deleted, not adapted for compat.
- **Green after every commit**: `dotnet build`, unit tests, `bun test` + type-check in `sdk/`, and the
  Docker build (frontend included) must pass, so every push deploys to v2 beta.
- **Regenerate clients on API changes** (`api.json` → Kiota TS client → Refitter C# client), or the
  frontend breaks at runtime rather than build time.
- **AOT stays.** New C# types are registered in the source-gen JSON contexts; no reflection-based libs.
- **Library grows from real use** (see memory/feedback): build the library steps v2's own deploy needs;
  extract more only while migrating real v1 pipelines; layered specializations only when used.
- Follow CLAUDE.md patterns: EntityStore/AttachmentStore, event hubs, transient services, sync core,
  `Olve.Results` everywhere, `IRunOnStartup`, structured logging, named endpoints, TUnit.

## Tracks

Two tracks run in parallel until they meet at the harness contract (B5/B6).

### Track A — TypeScript SDK + harness (`sdk/`)

| # | Work | Source | Done when |
|---|---|---|---|
| A1 | `sdk/` package `@olve/pipelines`: models, `fileRef`, `secret`, `optional`, `step`, `implement`, `pipeline()` builder (tuple mappers, `when`, `onPush`/`onPoll` with secrets, `onFailure`), `Ref<T>` | `spikes/sdk-types/src/sdk.ts` | compile-fail checker (51+ cases) passes on TS 7 + 5.9 |
| A2 | Harness runtime: `describe` (canonical manifest, tagged mappings, mapper source analysis, all describe-time checks), `run` (envelope + exit codes), `map-trigger`; strict output validation; model-name uniqueness | `spikes/compile/sdk/*` | modes + negative tests pass; manifest deterministic |
| A3 | Build tooling (`olve-build`): handler registry generation, per-handler hashes (contract files external), compile flags, PT_INTERP check, `step.tar.gz` | `spikes/compile/build.ts` | hash-demo behaviour reproduced in tests |
| A4 | `ctx`: `sh` (shell detection, embedded scripts copied out), `readText/readJson`, `files.fetch` + `output.file/dir` (fileRef with run/step/attempt/path/sha256), `http`, `retry`, `secrets`, `log`, `signal`, `run` | new | unit tests with `testHandler` |
| A5 | `testHandler(step, input, { ctx })` for `bun test` | new | used by A4/A6 tests |
| A6 | Library steps needed by v2's own deploy only: script, GitHub source fetch (tarball), image build (kaniko + layer cache), Helm deploy over SSH, HTTP health check — general function first, specialization only where used | ports of `.pipelines/scripts/*` | v2's own `pipeline.ts` (E1) uses them |
| A7 | `harness-base` image (static busybox + glibc `/olve/lib`) + build/push script; build/describe Job image pinning (`oven/bun:<pinned>-slim`) | `spikes/cluster/harness` | image in registry, one full design-B pod run validated |

### Track B — backend domain swap (`src/Olve.Pipelines`)

| # | Work | Done when |
|---|---|---|
| B1 | **Cut v1 leaf domains**: Building/ArtifactBundle + bundle store, Production, Processing + promotion, Trigger entities + trigger GitHub hooks, JobGroup*, DownstreamTriggerService, ManifestCompiler/YamlJsonBridge/PipelineManifest/PipelineDocument*/PipelineReconciler, StepConfiguration; trim snapshots/DI/CLI/tests; stub frontend detail views | build + tests green; v2 beta deploys and boots |
| B2 | **Manifest model + validator** (C# DTOs, source-gen JSON): all reconcile checks (§3.7), size caps, entity versioning (model/step content hashes → `<id>.<version>`) | table-tested |
| B3 | **Domain + persistence**: `PipelineVersion` (manifest, hash, commit, binary key), step/model versions, `Run` (number, pinned version, input, version stamp, supersededBy/rerunOf), `Attempt` (step name, n, status incl. Skipped, input hash, output ref), active/draining state; per-run objects under `runs/<p>/<run>/…` + index snapshot, readiness protocol | persistence tests (load/save/restart) |
| B4 | **Scheduler**: stage barriers, `when` evaluation, skip propagation (chained reasons), supersede, latest-wins keyed on step name, automatic-trigger de-dup, reruns (attempts) + continue; `input.json` assembly from tagged mappings over the pool; `JobRunner` re-keyed | scenario tests with `NoOpJobExecutor` |
| B5 | **Harness executor**: K8s Job spec (single harness-base init container, presigned GET for binary + input, per-step `secretKeyRef` for declared secrets only, `activeDeadlineSeconds`, TMPDIR, labels/runtimeClass), output collection (`output.json` + files → `runs/<p>/<run>/<step>/<attempt>/`), log capture, reattach-by-name; presigned URL support in the storage client | ported executor tests + one real step on beta |
| B6 | **Build/describe in reconcile + versions + switch**: subtree changed → build/describe Job (presigned PUT for binary, manifest from log sentinels) → validate → new version if hash changed → persisted `switch` state machine (Draining → Active); binding status shows manifest problems | integration test on beta with `.pipelines-test/` TS fixture |
| B7 | **Triggers**: push (binding webhook/deploy poll → map-trigger Job → validated input), poll (adapted `PollTriggerService`, per-trigger secrets), manual `POST trigger` with input validation (C# JSON Schema validator if AOT-safe, else a `validate-input` harness mode Job) | push → run end to end on beta |
| B8 | **API**: runs, attempts, outputs/files (§5.5 addressing), logs, rerun/continue/cancel, gate block/unblock, versions/manifest/switch, manifest validate endpoint (for `pl dev validate`), health summary over runs | regenerated clients; integration tests — **done 2026-10-21** (see B8 notes) |
| B9 | `onFailure` with typed `FailureContext` (handler run via harness, untracked); retention for runs/versions/binaries/file refs | tests |

**B8 notes (2026-10-21).** Endpoints (operationId): `ListRuns` (paged `RunSummary`, newest first),
`GetRun` (`RunDetail`: input, pinned version + commit, links by run number, `continuable`, the pinned
manifest's stage layout with `when` conditions and per-step latest attempt / reason / `held` / `blocked`),
`CancelRun`, `RerunRun`, `ContinueRun`, `ListRunAttempts`, `ListStepAttempts`, `RerunStep`
(`{continue}`), `GetStepOutput`, `ListStepFiles`, `GetStepFile` (`…/steps/{step}/files/{*path}?attempt=`,
streamed from Garage; `dir/` → `dir.tar`), `GetStepLogs`, `GetStepHistory` (by step name across runs,
`stepVersionChanged` markers), `GetPipelineHealth` (also embedded as `health` in `ListPipelineSummaries`),
`ListBlockedSteps` / `BlockStep` / `UnblockStep`, `ValidateManifest` (`POST /api/manifests/validate`,
always 200 with `{valid, manifestHash, errors, warnings}`). Decisions and deviations:
- **Continue** takes `{ input: { field: value } }`, merged field-by-field over the stored input and
  validated against the run's *pinned* input model (then the consumed-field rule). **Whole-run rerun**
  re-validates the original input against the version it binds to (defaults filled in).
- **Explicit cancel** also cancels in-progress attempts (their Jobs are deleted) — unlike supersede.
  A finished run with still-active attempts (e.g. superseded) gets only those attempts cancelled.
- **Logs** of a running attempt are the pod's current logs (`IAttemptLiveLogs`; olve-init's while the
  step container hasn't started); attempts that never ran return a one-line note, not an error.
- **Health** is over runs: running if any run is Running, else the latest Succeeded/Failed run
  (cancelled/superseded ignored). `PipelineSummary.status` now uses it; the per-step strip stays.
- GETs stay `AllowAnonymous` like the existing reads, except file downloads/listing (authenticated).
- Removed `ReconcileCoordinator`, `ReconcilePauseState` and the drain options.
- No new beta integration tests (the suite targets v1 beta); verified live on v2 beta against the
  fixture instead (gate hold → unblock, file fetch by attempt, rerun-and-continue re-running exactly
  the changed downstream steps, continue rejections, whole-run rerun, cancel mid-stage).
- Known: the generated TS client has a pre-existing `tsc` error on nullable polymorphic
  `PipelineActivation` members (also in `PipelineVersionsResponse` before B8); `vite build` is green.

### Track C — `pl` CLI

C1 v2 commands (`trigger --input`, `run list/get/output/file/continue`, `step rerun [--continue]`,
`pipeline manifest/versions/switch`, gate block/unblock, logs) over the regenerated contracts; remove
v1 commands — **done 2026-10-21**: `pl trigger <p> [--input json|@file] [--field k=v]` (fields typed by
the current version's input model), `pl run list|get|cancel|rerun|continue`, `pl step
output|files|file|logs [--follow]|rerun [--continue]|history`, `pl gate list|block|unblock`, `pl pipeline
health|versions|manifest [--version]|switch [--cancel-running]`, `pl dev validate`. Addresses
`<pipeline>#<run>[/<step>[@n][/<path>]]` and `<pipeline>/<step>`, pipeline by id or name (resolved via
`ListPipelines`). Hand-rolled AOT client (no Refit), 400 `ResultProblem[]` bodies shown one per line;
AOT publish has no trim warnings. No v1 client methods were left without an endpoint (`pl job` stays as
the attempt-by-id view). Verified live on v2 beta against the fixture. Open: `pl dev run --from` (C2)
still stubbed. C2 `pl dev describe|validate|run [--from|--docker]|pipeline` shelling out to the SDK's dev
runner (Bun). (C2 can start right after A2/A4; local runs are wanted early.)

### Track D — frontend

Rewrite pipeline/run/step views for the v2 model after B8 stabilises: stage graph with `when` blocks
and field-level wiring, versions + diff + switch, trigger form generated from the input model + run
plan preview, run view with typed input/output per attempt, step history by identity with version
markers. Keep shell, auth, docs, logs views.

**Track D notes (2026-10-21) — views built** (see `frontend/README.md` "Views"):
- **Pipeline list** with run health (last run, active version / switch in progress, running and held
  runs, closed gates) above the per-step strip.
- **Pipeline view**: active version's stage graph from the manifest (stage columns with barrier
  rules, nested `when` brackets labelled with their condition, hover traces producers/consumers,
  "Show wiring" lists every `{ref}`/`{const}` leaf), per-step gate block/unblock, recent runs,
  versions with step/model version changes, manifest diff between any two versions (line LCS over
  sorted-key JSON), switch with cancel-running.
- **Trigger form** generated from the input model's schema (`input` view, else `output`): strings
  with pattern/length, enums, numbers/integers with bounds, booleans, defaults, optional; anything
  else a JSON field. Client-side validation; every `ResultProblem` of a 400 is listed. **Run plan**
  preview greys out `when` blocks that won't match the current values (defaults filled in).
- **Run view**: input, graph coloured by step status (succeeded/failed/skipped+reason/held/
  blocked/pending/not-run/cancelled), attempts, typed input/output per attempt against the pinned
  models (file refs downloadable), files list + download, logs polled while running; cancel, rerun
  run, continue with field edits (only changed fields sent), rerun step (with continue);
  rerunOf/supersededBy links. **Step history** by name with definition-change markers.
- Toolchain: Kiota pinned to **1.36.1** in `.config/dotnet-tools.json` (1.30 typed nullable
  polymorphic refs as a union with an empty member → `tsc` error); `npm run build` runs `tsc` first.
  Actions/logs/downloads use a small `apiFetch` (the generated client maps a 400 to one
  `ResultProblem`, sends `JsonElement` bodies via `setContentFromScalar`, escapes `/` in `{*path}`).
  `npm run dev:v2beta` + dev-only `VITE_DEV_AUTH=bypass` (optional `VITE_DEV_TOKEN`).
- Verified in headless Chromium against v2 beta (fixture `ad01bc3f…`), light/dark and phone width,
  anonymously: every read view incl. skipped/cancelled/not-run/multi-attempt runs; action requests
  checked for shape (trigger body = input, continue = `{input: changed}`) with a faked 400 for the
  error display. Not exercised for real: actions with a token, file download, live log polling on a
  running attempt, draining display.
- Backend follow-ups (not done here): `TriggerRun`'s `JsonElement` body and 400s as arrays don't
  map well to generated clients (a typed request wrapper and a single problem-list error schema
  would let Kiota be used throughout).

### Track E — self-hosting and migration

E1 v2's own `.pipelines/pipeline.ts` (+ handlers, using A6 library steps) beside `config.yaml`. E2 bind
it on the v2 instance itself (beta-only first), verify, then retire the v1 deployer pipeline. E3
rewrite `docs/setup/*` for v2. E4 migrate other pipelines one by one (least critical first), growing
the library as patterns repeat; Olve.Pipelines prod last; `v2` → `main`.

## Milestones

| | Milestone | Needs |
|---|---|---|
| M1 | A TS pipeline from `.pipelines-test/` reconciles on v2 beta into a version and runs end to end (push → describe → stages → outputs in Garage) — **done 2026-10-21** (manual, push and poll runs on beta) | A1–A4, A7, B1–B7 |
| M2 | API + `pl` cover runs, outputs, reruns, switch; `pl dev` loops work (B8 API done) | B8, C1, C2 |
| M3 | Frontend renders the v2 model — **views built 2026-10-21** (see track D notes) | D |
| M4 | v2 deploys itself to beta from `pipeline.ts`; v1 deployer pipeline retired | A6, B9, E1, E2 |
| M5 | Other pipelines migrated; v2 promoted to prod and `main` | E3, E4 |

## Parallelisation (subagents)

- Start together: **A1+A2** (one agent: SDK + harness share types), **A7** (harness image), **B1**
  (backend cut — single agent; touches DI, persistence, CLI, tests, frontend stubs).
- After B1: **B2** and **B3** in parallel (pure validator vs entities/persistence), then **B4**.
- After A2: **A3/A4/A5**, then **C2**.
- **B5/B6/B7** sequential (shared executor + reconcile code), each verified on v2 beta.
- Agents on overlapping files never run concurrently; each finishes with build/tests green and a
  commit; the orchestrator pushes and checks the beta deploy.

## Open implementation questions (decide while building)

- C# JSON Schema validation for manual trigger input: AOT-compatible library vs harness Job.
- Exact Garage layout for binaries (`versions/<p>/<version>/step.tar.gz`) and presign expiry.
- Snapshot granularity (per-run objects vs per-pipeline snapshot) — pick per-run if index stays small.
- Output size cap value; secret masking approach in outputs/logs.
