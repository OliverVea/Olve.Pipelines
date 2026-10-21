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
| B8 | **API**: runs, attempts, outputs/files (§5.5 addressing), logs, rerun/continue/cancel, gate block/unblock, versions/manifest/switch, manifest validate endpoint (for `pl dev validate`), health summary over runs | regenerated clients; integration tests |
| B9 | `onFailure` with typed `FailureContext` (handler run via harness, untracked); retention for runs/versions/binaries/file refs | tests |

### Track C — `pl` CLI

C1 v2 commands (`trigger --input`, `run list/get/output/file/continue`, `step rerun [--continue]`,
`pipeline manifest/versions/switch`, gate block/unblock, logs) over the regenerated contracts; remove
v1 commands. C2 `pl dev describe|validate|run [--from|--docker]|pipeline` shelling out to the SDK's dev
runner (Bun). (C2 can start right after A2/A4; local runs are wanted early.)

### Track D — frontend

Rewrite pipeline/run/step views for the v2 model after B8 stabilises: stage graph with `when` blocks
and field-level wiring, versions + diff + switch, trigger form generated from the input model + run
plan preview, run view with typed input/output per attempt, step history by identity with version
markers. Keep shell, auth, docs, logs views.

### Track E — self-hosting and migration

E1 v2's own `.pipelines/pipeline.ts` (+ handlers, using A6 library steps) beside `config.yaml`. E2 bind
it on the v2 instance itself (beta-only first), verify, then retire the v1 deployer pipeline. E3
rewrite `docs/setup/*` for v2. E4 migrate other pipelines one by one (least critical first), growing
the library as patterns repeat; Olve.Pipelines prod last; `v2` → `main`.

## Milestones

| | Milestone | Needs |
|---|---|---|
| M1 | A TS pipeline from `.pipelines-test/` reconciles on v2 beta into a version and runs end to end (push → describe → stages → outputs in Garage) | A1–A4, A7, B1–B7 |
| M2 | API + `pl` cover runs, outputs, reruns, switch; `pl dev` loops work | B8, C1, C2 |
| M3 | Frontend renders the v2 model | D |
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
