# Olve.Pipelines v2 — implementation plan

Companion to [pipelines-v2.md](pipelines-v2.md) (the spec) and
[pipelines-v2-spikes.md](pipelines-v2-spikes.md) (spike findings). Work happens on the `v2` branch;
v2 deploys itself from this branch's `.pipelines/pipeline.ts` since milestone M4 (2026-10-21): the
pipeline lives on v2 prod (`pipelines-v2-private.ovea.pro`) since 2026-10-22, pushes deploy v2 beta
(`pipelines-v2-beta.ovea.pro`) and promotion deploys v2 prod; the v1 pipeline "Olve.Pipelines v2"
that ran `.pipelines/config.yaml` is paused, not deleted — see the E2 notes for the rollback.

## Ground rules

- **Fully breaking.** v1 elements that conflict with the spec are deleted, not adapted for compat.
- **Green after every commit**: `dotnet build`, unit tests, `bun test` + type-check in `sdk/`, and the
  Docker build (frontend included) must pass, so every push deploys to v2 beta.
- **Regenerate the TS client on API changes** (`api.json` → Kiota), or the
  frontend breaks at runtime rather than build time.
- **AOT stays.** New C# types are registered in the source-gen JSON contexts; no reflection-based libs.
- **Library grows from real use** (see memory/feedback): build the library steps v2's own deploy needs;
  extract more only while migrating real v1 pipelines; layered specializations only when used.
- Follow CLAUDE.md patterns: EntityStore/AttachmentStore, event hubs, transient services, sync core,
  `Olve.Results` everywhere, `IRunOnStartup`, structured logging, named endpoints, TUnit.

## Tracks

Two tracks run in parallel until they meet at the harness contract (B5/B6).

### Track A — TypeScript SDK + harness (`sdk/`)

| # | Work | Source (spike code, in git) | Done when |
|---|---|---|---|
| A1 | `sdk/` package `@olve/pipelines`: models, `fileRef`, `secret`, `optional`, `step`, `implement`, `pipeline()` builder (tuple mappers, `when`, `onPush`/`onPoll` with secrets, `onFailure`), `Ref<T>` | `d269107:spikes/sdk-types/src/sdk.ts` | compile-fail checker (51+ cases) passes on TS 7 + 5.9 |
| A2 | Harness runtime: `describe` (canonical manifest, tagged mappings, mapper source analysis, all describe-time checks), `run` (envelope + exit codes), `map-trigger`; strict output validation; model-name uniqueness | `d269107:spikes/compile/sdk/*` | modes + negative tests pass; manifest deterministic |
| A3 | Build tooling (`olve-build`): handler registry generation, per-handler hashes (contract files external), compile flags, PT_INTERP check, `step.tar.gz` | `d269107:spikes/compile/build.ts` | hash-demo behaviour reproduced in tests |
| A4 | `ctx`: `sh` (shell detection, embedded scripts copied out), `readText/readJson`, `files.fetch` + `output.file/dir` (fileRef with run/step/attempt/path/sha256), `http`, `retry`, `secrets`, `log`, `signal`, `run` | new | unit tests with `testHandler` |
| A5 | `testHandler(step, input, { ctx })` for `bun test` | new | used by A4/A6 tests |
| A6 | Library steps needed by v2's own deploy only: script, GitHub source fetch (tarball), image build (kaniko + layer cache), Helm deploy over SSH, HTTP health check — general function first, specialization only where used | ports of `.pipelines/scripts/*` | v2's own `pipeline.ts` (E1) uses them |
| A7 | `harness-base` image (static busybox + glibc `/olve/lib`) + build/push script; build/describe Job image pinning (`oven/bun:<pinned>-slim`) | `d269107:spikes/cluster/harness` | image in registry, one full design-B pod run validated |

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
| B9 | `onFailure` with typed `FailureContext` (handler run via harness, untracked); retention for runs/versions/binaries/file refs | tests — **done 2026-10-21** (see B9 notes) |

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

**B9 notes (2026-10-21).** No API changes.
- **onFailure.** A run going Running → Failed (`RunEvents.OnUpdated`) runs each failure handler of its
  *pinned* manifest whose scope (`steps`, null = any) contains one of the run's failed steps (each step's
  latest attempt that failed, not stale relative to `ResumedAt`); a run that failed without a failed step
  (unusable manifest) runs none. `FailureContext` (SDK `library/models.ts`): `pipeline {id, name}`,
  `run {id, number, address, versionStamp, trigger, url}`, `version {number, commit}`, `input`, `reason`,
  `failedSteps[] {step, attempt, address, reason, logUrl}` (only the steps in the handler's scope). The
  service projects it onto the pinned manifest's FailureContext schema and validates it first — a
  version built with an older SDK model is skipped with a logged reason, not launched into exit 65.
  Untracked and best-effort: a harness Job `olve-fh-<24 hex>` (deterministic per run + failure +
  handler, so a duplicate dispatch reattaches; a continued run that fails again dispatches again),
  role `on-failure` (v1 handler Jobs are `failure-handler`), the handler's declared secrets, no result
  upload, 7-day TTL; `input.json` and the log under `runs/<p>/<run>/_failure/<handler>/<id>/`; the URL
  Secret is deleted after. No attempt, no retry, no effect on the run. Links use `Links:BaseUrl` (else
  `Webhooks:PublicBaseUrl`), set on v2 beta. The fixture has `mode: "fail"` (echo throws) and an
  unscoped `log-failure` handler.
- **Retention** (`Retention:*`, **dry run by default** — set `Retention__DryRun=false` to delete). A run
  expires only when BOTH older than `MinRunAgeDays` (183) AND beyond its pipeline's `KeepLatestRuns`
  (20); never Running. Its objects (outputs, files, logs, `_failure/`) go after it is deleted through
  `RunService.Delete` and persistence is flushed; `run.json`, manifests and indexes are never deleted by
  retention (persistence owns them). `step.tar.gz` (both key shapes) is kept for active/draining
  versions and versions of retained runs, else deleted after `BuildGraceDays` (7). Leftovers: objects of
  runs no longer in state after `OrphanGraceDays` (7), leftover `result.tar.gz` of finished attempts
  (`ResultArchiveGraceHours`, 24), stale `triggers/…/event.json`. K8s: URL Secrets (`olve-urls-<attempt>`,
  `olve-{map,fh,build}-…-urls`) once their Job is gone/finished and their attempt is not active; leaked
  (running, attempt not active) step Jobs and TTL-less finished Jobs — only with a v2 role and a
  pipeline of this instance (the runner namespace is shared with v1). Startup sweep after
  `StartupDelayMinutes` (2), then every `IntervalHours` (24); per-category counts every sweep, each
  item in dry run. Version entities and their manifests are kept (small; history).
- **Poll triggers** persist their last value per (pipeline, trigger) in `pipeline-state.json`
  (`PollTriggers`): after a restart the first fetch is compared against it, so a commit pushed just
  before a redeploy still triggers; only a new trigger or a changed URL seeds.
- Removed the test-only `RunQueryService.GetStepHistory`/`StepHistoryEntry`.
- Verified live on v2 beta: fixture run #20 (`mode: fail`) failed at echo → `olve-fh-30657fc6…`
  (role `on-failure`) ran `log-failure` and printed the full context (run address, version 5 + commit,
  input, reason, echo@1 with a working log link); log persisted under `_failure/`, URL Secret gone. A
  dry-run sweep with temporary aggressive thresholds (latest 3, 0 days) listed 17 runs / 186 objects and
  4 unused ~44 MB binaries it would delete, keeping the active version's; K8s listings worked. After a
  redeploy the poll trigger compared against its persisted value instead of re-seeding. Real deletion
  is unit-tested only (beta keeps dry run).
- Known gaps: a dispatch lost to a crash between the run failing and the Job being created is not
  retried (best-effort by design); switching back to a version whose binary retention removed fails at
  launch (only possible for a version no retained run used, after the grace); handler Jobs do not
  appear in the API/UI (log is in Garage only).

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
- Backend follow-ups: `TriggerRun`/`ContinueRun` bodies are typed now (E2 notes); 400 arrays still
  don't map to Kiota errors (open decision in the E2 notes).

**For the UI: live events (2026-10-22)** — `GET /api/events[?pipeline=<id>]` (SSE, anonymous; use a
plain `EventSource`, not the Kiota client). Event types and data (camelCase): `run`
`{pipelineId, run, status}`, `attempt` `{pipelineId, run, step, attempt, status}`, `version`
`{pipelineId, version, change: added|active|draining}`, `gate` `{pipelineId, blockedSteps}`,
`binding` `{pipelineId, state: reconciling|Success|Error|NeverRun|removed}`, `pipeline`
`{pipelineId, change: added|updated|removed}`; plus `hello` (every connect), `resync`, `ping`
(15 s keep-alive). Contract:
- Open the stream **before** the first fetch of a view; on a relevant event, `reload()` that view
  (debounce a burst, e.g. 250 ms — a stage settling fires several `attempt`/`run` events at once).
  Events are notices, never state: always re-fetch.
- Relevance: list → any `run`/`pipeline`/`version`/`gate`; pipeline view → its `pipelineId`; run view
  → `run`/`attempt` with its run number; step history → `attempt` with its step; binding badge →
  `binding`.
- `resync` → reload everything on screen. No replay; ids are `<boot>-<seq>`; the browser sends
  `Last-Event-ID` itself on reconnect. A client 256 events behind is disconnected and reconnects.
- Logs of a running attempt: `EventSource` on `…/runs/<n>/steps/<step>/logs/stream[?attempt=]` →
  `log` `{text, reset}` (append, or replace when `reset`) and `end` `{status}`; close on `end` (else
  the browser reconnects and replays the whole log).

### Track E — self-hosting and migration

E1 v2's own `.pipelines/pipeline.ts` (+ handlers, using A6 library steps) beside `config.yaml`. E2 bind
it on the v2 instance itself (beta-only first), verify, then retire the v1 deployer pipeline. E3
rewrite `docs/setup/*` for v2. E4 migrate other pipelines one by one (least critical first), growing
the library as patterns repeat; Olve.Pipelines prod last; `v2` → `main`.

**E2 notes (2026-10-21) — M4 done: v2 beta deploys itself.**
- **Binding** on v2 beta: pipeline `f26d19cf-810a-4fb8-b5af-e61ca73b1bae` ("Olve.Pipelines v2"), binding
  `56af939f-2f15-4ebb-8892-a1b2e4894c15`, `OliverVea/Olve.Pipelines@v2`, path `.pipelines`, trigger
  `poll` (no public webhook route), no credentials key (public repo; a key named before it exists fails
  the build Job). `pl dev validate --dir .pipelines` was valid first (kinds gitHubSource, imageBuild,
  custom, helmDeployOverSsh — all in `StepKinds.Known`; one `output-unused` warning on deploy-beta).
  Secrets GITHUB_TOKEN + SSH_PRIVATE_KEY were piped from v1's `olve-pipeline-c6f4abe1…` (olve-runners)
  through `pl secret set` (stdin, never `$(…)`: that strips the SSH key's trailing newline) into
  `olve-pipeline-f26d19cf810a4fb8b5afe61ca73b1bae` in olve-runners-beta; sha256-compared equal.
- **Result collection is memory-safe for big outputs** (checked before running kaniko through it):
  `HarnessResultCollector` copies `result.tar.gz` to a temp file, streams the gzip/tar, stages each
  `files/` entry to disk and PUTs it by `FilePath`; only the capped `output.json` is buffered. Peak disk
  ≈ archive + one entry in the container's writable layer (no ephemeral-storage limit on the chart).
  The image tar turned out small anyway (AOT image: `image.tar` 50 MB).
- **Run #1** (manual, sha 321b136, already deployed by v1 — a dry run of self-restart): source 6 s,
  build 4m22s (kaniko layer cache push to registry.ovea.pro from olve-runners-beta worked), test 1m10s,
  deploy-beta 21 s; run 4m52s, Succeeded. deploy-beta replaced the very pod running the run; the new pod
  logged `Reattaching to K8s Job … deploy-beta`, collected it and finished the run (`run.json` in Garage:
  Succeeded); `/api/health` 200; image `olve-pipelines-v2:2026.10.21.1`.
- **v1 deployer paused** by blocking its deploy-beta promotion gate (v1 bindings have no off switch;
  `IsBlocked` gates every path that creates a v1 processing job, and the block survives reconcile and
  restarts). v1's poll still runs the production build on each push — it builds but never deploys.
  The pipeline, its binding, its secrets and `config.yaml` + `scripts/` stay as the recovery path.
- **Rollback (re-enable the v1 deployer)**, with a v1 prod token (`auth.ovea.pro`, client credentials
  from `apps/olve-pipelines-oidc`) — and first pause the v2 self-deploy so two deployers don't race
  (`pl gate block "Olve.Pipelines v2/deploy-beta"` against v2 prod, where the deployer lives now):
  ```sh
  V1=https://pipelines-private.ovea.pro
  # unblock; this immediately promotes v1's latest completed production bundle if one is due,
  # which may be older than the branch head — check the response's `promotion`
  curl -X PUT $V1/api/processing-steps/5b0acf91-f166-4c41-ae54-6e2d6410fd34/promotion \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"blocked":false}'
  # then build + deploy the current head through v1
  curl -X POST $V1/api/pipelines/c6f4abe1-4821-43d7-afd6-bd9f25414b75/trigger/production -H "Authorization: Bearer $TOKEN"
  # check which image landed
  kubectl -n apps-beta get deploy olve-pipelines-v2 -o jsonpath='{.spec.template.spec.containers[0].image}'
  ```
  (State check: `GET $V1/api/pipelines/c6f4abe1-4821-43d7-afd6-bd9f25414b75/processing/promotions`.)
- **Self-deploy from a push** works end to end — poll → push trigger → (reconcile → version) → run →
  self-restart → reattach. The first two push runs needed a manual rerun (kaniko hang, gap 2); the
  third went green unattended:
  - 35faafc (API change below) → the deploy poll fired the push trigger → run #2; kaniko hung at
    "Unpacking rootfs" → cancelled → rerun #3: 4m34s, Succeeded, pod on `olve-pipelines-v2:2026.10.21.3`.
    The deployed binary binds the new bodies (fixture trigger #23 started; continue reaches the domain
    checks; a non-object trigger body is now a framework 400).
  - 69df341 (this doc + a `pipeline.ts` comment) → reconcile built version v2 (all step versions bumped,
    see gap 1) → push run #4 hung the same way → cancelled → rerun #5: 1m47s (build 1m18s, fully
    cached), Succeeded, pod on `…:2026.10.22.5`, new pod reattached and collected deploy-beta,
    `run.json` Succeeded, health 200.
  - a1158df (docs only, no new version) → push run #6, **unattended**: 1m55s (build 1m24s), Succeeded,
    pod on `…:2026.10.22.6`, reattached deploy-beta, health 200. Its build started ~8 min after v1's
    production build of the same commit (consistent with the gap-2 hypothesis).
  - v1 meanwhile ran only production builds for both commits (bundles 5ef5ba20, …) and **never
    deployed** (last v1 deploy job still 23:07 / bundle ca936b0d).
- **Track D follow-up (API)**: `TriggerRun`'s body is `TriggerRunRequest`, a free-form object
  (`[JsonExtensionData]`; the wire shape is still the input object itself) and `ContinueRunRequest.input`
  is an object of fields, so Kiota generates AdditionalDataHolder models and sends real object bodies
  (checked with a stubbed fetch: `{"sha":…}` and `{"input":{…}}`). The 400s were already documented as
  `ResultProblem[]` — exactly what the API returns — but **Kiota cannot model an array error**: it uses
  the item type, and at runtime the array lands in the error's `additionalData` under `"0"`, `"1"`, …
  with `message` undefined. Surfacing every message through Kiota needs either an object-shaped 400
  (a wire change in every endpoint — Olve.MinimalApi's `WithResultMapping`, `pl`, `apiFetch`, Refitter)
  or a frontend helper that reads `additionalData`; not done — **open decision**. Mutations stay on
  `apiFetch`.
- **Garage**: a self-deploy run stores ≈ 55 MB (`image.tar` 50.4 MB, `tree.tar` 4.1 MB, logs/JSON
  small); the binary is ≈ 44 MB per version (only `.pipelines/` commits make versions). Bucket total
  377 MB after these runs. 20 retained runs + a few binaries ≈ 1.2–1.5 GB — fits easily — **but**
  retention deletes a run only when it is BOTH beyond the latest 20 AND older than 183 days, and beta
  runs `Retention__DryRun` (nothing deleted). 20 Gi ≈ 390 self-deploy runs: ~2–3 weeks at today's
  commit rate, ~2.5 months at 5 runs/day — under the 183-day floor. **Decision needed** (not changed
  here): a shorter `MinRunAgeDays` for beta, dropping big file outputs (image tars) earlier than the
  run record, a bigger PVC, and flipping `Retention__DryRun=false`. (The `bulk` StorageClass may not
  enforce the 20 Gi size.)
- **Gaps (1) and (2) below fixed 2026-10-22** (df07a3c, d5ff772, b7ecabf):
  - **kaniko stall → self-heals.** Design: fail fast + retry the *attempt*, not kaniko. (a) The
    scheduler now honours the manifest's `retries: n` (spec §5.2; it was parsed/validated/versioned
    but nothing read it): `StageScheduler` reschedules a failed step while the trailing streak of
    failed attempts since the run was last resumed is ≤ n (cancelled/obsolete attempts and
    scheduler-side failures such as an oversized input are never retried; a continue resets the
    count; failure handlers still fire only when the run fails). (b) `ctx.exec`/`ctx.sh` gained an
    `idleTimeoutSeconds` inactivity watchdog (child output is piped and forwarded; TERM, KILL 10 s
    later; `CommandStalledError`). (c) `steps.imageBuild` runs kaniko under it by default
    (`stallTimeoutSeconds` 300 — the longest silent phase of a normal build is the AOT "Generating
    native code", ~2 min) and defaults to `retries: 2`. (d) `.pipelines` build `timeoutSeconds`
    3600 → 900 as the backstop. **Why not retry inside the pod:** kaniko unpacks the base image over
    the container root and only wipes it between stages, so a killed kaniko leaves a partial rootfs
    that a second kaniko in the same container would snapshot as "base" (files silently missing from
    layers). A fresh attempt is a fresh pod, and its image tag gets `-attempt<n>`. The watchdog
    itself is compatible with kaniko: the harness runs from the `/olve` emptyDir mount, which kaniko
    never touches, and the pipes survive kaniko rewriting `/`.
  - **SDK changes reach the binary.** The GitHub config source's cursor is now the newest commit
    across the bound directory **and** the `file:`/`link:` dependencies of its `package.json`
    (dependencies/devDependencies/optionalDependencies, resolved relative to it, normalised; absolute
    specs, paths escaping the repo and paths inside the bound directory are dropped). Each watched
    path has its own conditional request (free 304s); `package.json` is re-read only when the bound
    directory's own cursor moves; the per-path state rides in the opaque ETag
    (`ConfigCursorState`). Direct dependencies only (an SDK's own `file:` deps are not followed).
  - **Live, run #8** (push d5ff772; reconciled + run by the *old* backend, version 3 from df07a3c):
    4m53s, build 4m23s, Succeeded, deployed `…:2026.10.22.8`, health 200 — the new backend.
  - **Live, run #9** (b7ecabf, an `sdk/`-only commit: imageBuild's `retries` default): reconcile-now
    moved the cursor to b7ecabf (`.pipelines/` was last touched by df07a3c) and built **version 4**
    whose build step carries `retries: 2` — the SDK change reached the step binary. Push run #9 then
    hit a **real MCR stall**: build attempt 1 printed "Unpacking rootfs …" at [0007] and nothing
    else; the watchdog killed it at 300 s (`CommandStalledError`, attempt failed 01:22:10); the
    scheduler started attempt 2 at once, which built in 4m28s (tag `2026.10.22.9-attempt2`);
    deploy-beta 24 s; run Succeeded unattended, pod on `…:2026.10.22.9-attempt2`, health 200. A
    stall now costs ~5 min instead of an hour plus a manual cancel/rerun.
  - Left open: a step version bump on every SDK change (all step versions moved v3 → v4, gap 1's
    `sdkHash` note); the stall source itself (fixed below: base-image mirror); the 10 s TERM→KILL
    grace is an SDK constant (the 300 s window is `stallTimeoutSeconds`).
- **kaniko stall root cause: gVisor × mcr.microsoft.com; fixed with a base-image mirror**
  (2026-10-22, dfc4458).
  - Before: 5 of 17 self-deploy build attempts stalled (#2, #4, #9 a1, #12 a1+a2; #13/#14 clean),
    every one at the first "Unpacking rootfs" (the `dotnet/sdk:10.0-noble` pull, ~350 MB, 11
    layers, served straight from mcr.microsoft.com = Azure Front Door 150.171.69/70.10). Never at
    `node:22-slim` (Docker Hub) or the cache layers from registry.ovea.pro.
  - **Not v1's concurrent build.** v2 #11 built clean while v1's build of the same push started
    and hung at the same point (v1 pod `olve-19f8da0d…` in olve-runners, hung since 07:27:57 and
    still running an hour later); #9 a1 and #12 a2 stalled with no v1 pull in flight, #9 a2
    overlapped one and was clean. v1's kaniko pods are gVisor too and stall on their own (68 min on
    10-21 18:18, plus the current one, of 33 in the last 7 days). v1 was left as is.
  - **gVisor is the trigger.** Ad-hoc pods in olve-runners-beta, same node/namespace/NetworkPolicy,
    side by side, `FROM mcr.microsoft.com/dotnet/sdk:10.0-noble` + `RUN true`, kaniko `--no-push`
    in a loop with a 120 s no-output watchdog: **gVisor 3 of 20 stalled (two rounds), runc 0 of 8**. Each stall
    is one ESTABLISHED socket to 150.171.70.10/69.10:443 with Recv-Q 0, no data, forever.
    `crane copy` of the same image under gVisor (concurrent, fast consumer) took 11 s — the stall
    needs kaniko's slow consumer (stream + extract one layer at a time).
  - **No kaniko flag helps:** `--image-download-retry` / `--image-fs-extract-retry` / `--push-retry`
    fire on errors; a hung body read never errors and kaniko has no read timeout.
  - **Fix.** `steps.imageBuild` gained `registryMap` (kaniko `--registry-map`, falls back upstream
    when the mirror lacks an image). `.pipelines` maps `mcr.microsoft.com` and `index.docker.io` to
    `registry.ovea.pro/olve-pipelines-v2/mirror/<registry>`; a custom `mirror-base-images` step
    (crane:debug, parallel to `source`) runs `crane copy --platform linux/amd64` for the
    Dockerfile's three FROMs — ~2 s when unchanged, non-fatal on failure (stale tag or upstream
    fallback). Same test, gVisor via the mirror: **0 of 22 stalled**. Live: runs #15–#19 (version
    6) built first try (3m44s–4m46s), all three FROMs "from mapped registry registry.ovea.pro".
  - The mirror is ~420 MB per base-image version in the registry's 10 Gi PVC (shared with the
    kaniko cache; registry:2 has no GC configured). Keep the step's image list in sync with the
    Dockerfile's FROMs (a missing one still builds, from upstream).
  - Alternative (not applied, homelab infra): a separate `registry:2` instance in pull-through mode
    for mcr.microsoft.com (`REGISTRY_PROXY_REMOTEURL`; never on the existing registry, which would
    then refuse pushes), running under runc, would replace the mirror step.
  - **Pull-through caches exist** (2026-10-22, Olve.Homelab 119c192): `registry:3` proxies under
    runc on the `bulk` tier, 720h TTL — `mcr-cache.ovea.pro` (mcr.microsoft.com) and
    `hub-cache.ovea.pro` (Docker Hub, anonymous). From a gVisor runner pod, kaniko with
    `--registry-map mcr.microsoft.com=mcr-cache.ovea.pro` pulled `dotnet/sdk:10.0-noble` in 41 s
    cold / 20 s warm (the mirror: 17 s); `node:24-alpine` via `index.docker.io=hub-cache.ovea.pro`
    10 s / 4 s. The registry now has nightly GC (`registry-gc`: `*/cache` tags > 3 d,
    `*/mirror/*` > 14 d).
  - **Done** (2026-10-22, SDK `2.0.0-beta.2`): the caches are `imageBuild`'s default `registryMap`
    (`DEFAULT_REGISTRY_MAP`; a map replaces it, `{}` turns it off), and `mirror-base-images` is gone
    from Olve.Pipelines, Olve.Template.Api and homelab-dashboard. Caches never block a build: before
    kaniko starts, each mirror host and the `cacheRepo` registry gets a 5 s `GET /v2/` (200/401 =
    up) and an unreachable one is dropped with a log line (nightly GC, maintenance).
    `--skip-push-permission-check` keeps kaniko's startup check of the cache repo from exiting;
    from kaniko's source, a mirror that can't serve a manifest falls back to the original registry,
    a cache lookup error is a miss and a cache push error a warning. A cache dying mid-download
    fails or stalls the attempt; `retries` covers it.
- **v2 beta serves the v2 `pl`** (2026-10-22, 0eca595). A custom `publish-cli` step (stage after
  `deploy-beta`, same `when`; `dotnet/sdk:10.0-noble` + apt clang/zlib1g-dev) publishes linux-x64
  AOT + win-x64 single-file stamped with the run version and returns them as **output files**
  (`CliRelease`) — no storage credentials in the step pod. `GET|HEAD /download/{asset}` streams the
  latest successful `publish-cli` attempt of `Distribution__CliPipeline` ("Olve.Pipelines v2"),
  only allow-listed names, everything else under `/download/` a 404 (it used to fall through to the
  SPA with 200 for HEAD and multi-segment paths). Retention pins that run (`RetentionInputs.PinnedRuns`,
  same `CliReleaseQuery`), so beta's 14 days / latest 3 can't delete the served binary.
  `/install.sh` / `/install.ps1` are served with the instance as the default. `pl --version` added.
  The download route left the OpenAPI document (Kiota client regenerated). **Live, run #12**
  (reconciled into version 5): build needed 3 attempts (kaniko stalls, self-healed), deploy-beta 21 s,
  publish-cli 2m03s; `HEAD /download/pl-linux-x64` 200, 9.5 MB, `x-olve-pl-run: 12`; the served
  binary's sha256 equals the output ref; `curl …/install.sh | sh` installed `pl 2026.10.22.12`, which
  lists the v2 commands and ran `pl run list` against v2 beta. Open: v1 prod's `install.sh` (raw
  GitHub, `main`) still installs v1's `pl`; the v2 `pl`'s built-in default URL is still v1 prod
  (`pl login --api-url` needed, the scripts print it); macOS/arm builds were never published.
- **Gaps** (as found on 2026-10-21; (1) and (2) fixed above): (1) the config cursor watches only `.pipelines/`, but `pipeline.ts` builds against
  `../sdk` — an SDK-only commit does not rebuild the step binary, and reconcile-now does not help (the
  build Job is per config commit, `olve-build-<p12>-<sha12>`, so it rebuilds the same old checkout).
  Proof: versions v1 (aeb5960) and v2 (69df341) differ only in `sdkHash` and handler hashes, i.e. runs
  #1–#3 ran the SDK from before b5b3f93. Only a `.pipelines/` commit picks up SDK changes. (2) **kaniko
  hangs at "Unpacking rootfs"**: 2 of 6 builds (#2, #4 — the first two push runs; push run #6 was fine); the stalled pod idles (3m
  CPU) with one ESTABLISHED, empty connection to 150.171.70.10:443 (Microsoft — the MCR base-image
  pull stalled and nothing times it out). Hypothesis (n=5): both hung builds started within ~2 min of
  v1's production build of the same commit (same base image, node, egress); the green ones started 6+
  min clear — if so, stopping v1's (now useless) production builds would remove it. (Disproved 2026-10-22: the trigger is gVisor; see the kaniko stall root cause note.) v1 itself
  has had one 4120 s production job out of 41, so MCR stalls are not v2-only. Impact: one attempt per
  (pipeline, step) may be InProgress (`JobRunner`), so a hung build also holds the next push's build
  until `timeoutSeconds: 3600` expires. Recovery: `pl run cancel <p>#n && pl run rerun <p>#n`.
  Mitigations to decide: a build timeout near the normal ~5 min (e.g. 900 s), an inactivity
  watchdog/retry in `steps.imageBuild`, mirroring the base images in registry.ovea.pro, or stopping
  v1's production builds (e.g. deleting its production steps from the v1 pipeline — not done: the
  rollback relies on them). (3) A cancelled attempt can leave a `result.tar.gz` (retention's 24 h
  leftover sweep covers it once dry run is off).

### v1-leftover rewrite (2026-10-22)

- `/api/jobs*` and `pl job *` are gone (`CancelJob` cancelled an attempt outside the scheduling lock).
  Replacements: `POST …/runs/{n}/steps/{step}/cancel` (`RunCancellationService.CancelStep`; the
  run ends Cancelled at the stage barrier like any cancelled attempt, rerun `--continue` re-opens
  it; `pl step cancel`) and `GET /api/attempts?status=active` (`pl attempts`). CLI `Job` DTOs are
  `Attempt`. The orphan `job-logs-view` is removed.
- `GET /api/pipelines/summary` is `{id, name, description, repo, health}` (no step strip).
- `GET …/secrets` is `[{name, description, declaredBy, isSet}]` (`PipelineSecretService`: active
  manifest + binding credentials key + K8s; `isSet` null when K8s is unreadable). Binding status
  no longer carries secrets; deleting a pipeline deletes its `olve-pipeline-<id>` Secret.
- Binding `lastDeployedSha`/`lastSyncedSha` are `lastPushSha`/`lastReconciledSha` (old snapshot
  names still load); status adds `reconciledVersion` and `activation`. `pl secret`, `pl binding`
  and `pl pipeline delete` take pipeline names.
- Dead code removed: `IPipelineSecretStore`, `ConfigFetch.Changed.Files`, bare Jobs,
  `PipelineService.SetName`, the public `/api/webhooks/github` ingress path, `pl bootstrap`'s
  legacy-MinIO guard. The v2 chart no longer has MinIO templates or `minio:` values.
- Deferred: renaming `JobQueueService` / `JobLogService`.

### Error model (2026-10-22)

- **Typed problems** (`Shared/Problems`): Olve's `NotFoundProblem` (404, Olve.* 0.55), `ConflictProblem`
  (409: inactive version, blocked step, active attempts, superseded, already bound/exists),
  `ValidationProblem` (400) and `UnavailableProblem` (503: K8s/Garage/GitHub). Validation and conflict
  problems carry an optional `Field` (`input.sha`; `JsonSchemaValidator` and the continue
  consumed-field check set it). Plain `ResultProblem` stays 400.
- **One body for every non-2xx** but 401/403 (empty: no auth code):
  `application/problem+json` `{type, title, status, detail, errors: [{code, message, field?}], traceId}`,
  codes `not-found | conflict | invalid | unavailable | internal`. `WithApiResultMapping` replaces
  Olve's `WithResultMapping` and declares `ApiProblem` for 400/404/409/500/503 (Kiota generates a
  typed error). **Status precedence**: 404 > 409 > 503 > 400 — a missing resource or a conflicting state
  can't be fixed by retrying; prepended plain context never downgrades the status.
- Severity, source, tags and exception summaries are never returned; the mapping logs them once
  (`LogProblems`). `traceId` is the W3C trace id; console scopes are on, so it greps to the log lines.
- `UseApiErrors`: unhandled exceptions → 503 + `Retry-After` for transient ones (`HttpRequestException`
  5xx/429/no status, S3 5xx, timeouts, socket/IO), 500 otherwise, generic text, logged once with the
  stack; binding failures (`ThrowOnBadRequest`) → 400; empty-bodied `/api` errors get the shape.
- Rejected: array bodies (no place for status-level fields; Kiota maps them to one object), tag-string
  codes on `ResultProblem` (stringly typed, easy to drop on `Prepend`), Olve's `[GenerateResult]`
  unions per endpoint (a type per endpoint for what is a cross-cutting mapping).
- Later: lift the problem types and the mapping into Olve.Results / Olve.MinimalApi.

### v2.1 — SDK publishing (2026-10-22)

- The SDK is published to npm as **`@olivervea/pipelines`** (`@olve` on npm is someone else's).
  Pipelines keep importing `@olve/pipelines` via an alias:
  `"@olve/pipelines": "npm:@olivervea/pipelines@<version>"`. The alias name is load-bearing
  (`olve-build` generates `@olve/pipelines/harness` imports). In-repo `.pipelines`/`.pipelines-test`
  stay on `file:../sdk`.
- The package ships TS sources (`src/`, `manifest.schema.json`, README, LICENSE): Bun runs them, and
  `harness.sdkHash` hashes `src/`, so npm and `file:` installs of one version hash identically
  (checked: same `sdkHash` from the packed tarball and from `file:../sdk`).
- **Version scheme**: `2.0.0-beta.N` on dist-tag `beta` (`publishConfig`) until v2 prod, then `latest`.
  Release = bump `sdk/package.json` `version` and push.
- **Publish step**: `publish-sdk` (`oven/bun:1.4.3-slim`, beside `publish-cli` in the `when deployTo = prod`
  block: publishing is a prod action, promoted by continuing a tested run with `deployTo=prod`) skips
  when `name@version` is on npm (registry GET; 404 = unpublished) and fails when `NPM_TOKEN` is unset;
  otherwise `bun install --frozen-lockfile`, `bun run check` (clean env) and `bun publish`
  (token via `NPM_CONFIG_TOKEN` in the env only).
- **Optional secret mounts**: declared step secrets are `secretKeyRef optional: true`. A missing key no
  longer fails the pod with `CreateContainerConfigError`; the env var is unset and `ctx.secrets.X`
  throws on access, so a handler can check `process.env.X` and skip.
- **Build Job**: the hardcoded `sdk/` install is gone. The script reads the binding's `package.json`
  and runs `bun install --frozen-lockfile --production` in each local `file:`/`link:` dependency
  directory (exit 78 if one resolves outside the repo); registry deps need nothing extra.
- **Live**: run #23 (v8) published `2.0.0-beta.1` (`beta` tag); the registry served 404 for ~5 min
  after the publish (CDN), longer for Bun's abbreviated metadata.
- Follow-up: switch to npm trusted publishing via GitHub Actions (OIDC, provenance, no token) now that
  the token path works and the package exists (npm configures a trusted publisher only on an existing
  package: npm ≥ 11.5.1, Node ≥ 22.14, `id-token: write`).

### Trigger sources (2026-10-22)

- **No per-binding deploy trigger.** The webhook / webhook-only / poll choice was dropped: every binding
  is polled (an unchanged branch is a free 304) and gets a GitHub push hook when the instance has
  `Webhooks:PublicBaseUrl` and the binding has credentials. Whether a push starts a run is the
  manifest's `onPush`.
- **Two primitives** (`Sources/`, phase 1, no behaviour change): `PollScheduler` is the one polling loop
  over `IPollSourceProvider`s (`PollTriggerService` for manifest `onPoll`, `DeployPollService` for the
  gated config-then-push cycle per binding); `WebhookDelivery` + `WebhookVerification` (HMAC-SHA256
  with header/prefix, bearer) receive webhooks, GitHub's signature being an HMAC preset.
- **Config-before-run for every automatic trigger** (phase 3a): on a bound pipeline, poll and webhook
  trigger runs start under the binding's gate once its current config commit is applied
  (`IConfigGate`, reconciling it first). A poll value is recorded only then (a change seen while the
  config is unapplied is picked up by a later poll); a webhook delivery in that state is dropped and
  logged. Push already ran under the gate.
- **Webhook triggers** (phase 2): `onWebhook({ name, verify }, ({ headers, body }) => input | null)`;
  manifest kind `webhook` with `verify` `hmac-sha256` (header, prefix) or `bearer`, both on a declared
  secret. `POST /api/hooks/{pipeline}/{trigger}` (public) verifies with the secret, answers 202 and maps
  in the background; a `null` mapping is a `skipped` envelope and starts nothing. Runs are `Webhook`
  (automatic: supersede + de-dup). The chart's public ingress routes `/api/hooks` too.
- **Push as a trigger source** (phase 3b): the binding keeps only config sync (`DeployPollService`).
  `PushTriggerService` is a poll source per bound pipeline whose version declares `onPush`, nudged by the
  binding's GitHub hook (one hook per repo feeds config and push); each check applies the config
  through `IConfigLock`, then compares the head with `LastPushSha` (first observation seeds). "Reconcile
  now" is config only. Repo, branch and credentials stay binding data, so `onPush` takes no source
  config of its own.
- **Next**: phase 4 a Triggers page in the UI.
- **2.1 candidate (asked for)**: a step's destination as a link on the overview, e.g. a deploy step
  links to the deployed site. Likely an SDK step field or an output convention (a URL the step
  declares or returns), shown on the step card and step page.

### Blocking, not gates (2026-10-22)

The operator hold on a step was called a "promotion gate" (`pl gate`, `/gates`, event `gate`).
Renamed to **blocking** (`pl step block|unblock|blocked`, `GET …/blocked-steps`, event `blocked`,
`StepBlockService`, `PipelineBlockedSteps`; `pipeline-state.json` writes `BlockedSteps` and still
reads the former `Gates`). Blocking is for working on a pipeline or its target and for incidents, used
sparingly, not an approval mechanism. **Gate** is reserved for gating inside a pipeline: a step or
stage that decides whether a run goes on. The per-binding config-before-run lock is `IConfigLock`.

### v2.2 — candidates

- **Spec-first API (TypeSpec)**, as in Olve.AgentRuntimeManager: the HTTP API is declared in `.tsp`
  (one file per area), and OpenAPI, the backend's C# surface and a typed TS client are generated
  from it. Today the API is code-first (minimal APIs → `api.json` → Kiota), and the frontend's
  mutations bypass the generated types. Wanted by the user for readability; do it in a quiet period,
  not alongside other work. Decide whether ARM's `typespec-arm-csharp` emitter fits (it assumes
  ARM's error envelope; this API answers `ApiProblem`).
- **Gating steps**: library steps that decide whether a run goes on, beyond the stage barrier on a
  test step: manual approval (the run waits for an approve/reject in the UI or `pl`, with who and
  when recorded), visual regression (baseline diff, approve to accept), external checks. Replaces
  "keep `deploy-prod` blocked" as the way to approve a prod deploy.

- **Manifest preview** (frontend): a `/preview` route that takes a manifest JSON (paste, file drop or
  `?manifest=<url>`, e.g. from `pl dev describe --json`) and renders the pipeline overview from it
  with every step pending, without API calls. It shows a pipeline as the app will before it is
  deployed. Build it once the overview redesign settles.
- **Event stream filtering**: `/api/events` filters by pipeline only (`?pipeline=`). Add filters by
  event type and by run (e.g. `?types=run,attempt&run=12`), so a run page or `pl wait` gets only what it
  re-fetches on.
- Environments/tags, problem types lifted into Olve.MinimalApi, metrics, a `pl` smoke step, Blazor
  revisit.

### v2.3 — output caching

- Opt-in per step: `cache: true` keys on the whole input; `cache: { key: { cli: [source, s => s.hashes.cli] } }`
  keys on a mapped subset, recorded as tagged mappings so the service computes it without TypeScript.
  Off by default (deploys must always run).
- `gitHubSource` option `hashes: { cli: ["src/Olve.Pipelines.Cli", …] }` adds content hashes of path
  sets to `Source.hashes`; whole-input keys never hit for source-based steps because `sha`/`tree`
  change every commit.
- A succeeded attempt with the same step name, step version (includes the handler hash) and key is
  reused: a succeeded attempt with `cachedFrom`, output by reference, no Job. Retention keeps runs
  whose files are still referenced; `/download` pinning follows the refs.
- Motivating case: `publish-cli` (~3.5 min of a ~8 min run) on commits that don't touch the CLI.
  Groundwork exists: attempts store `InputHash`.

### v2.3 — rollback monitoring

- A deploy step declares what healthy means and for how long, in the SDK:
  `monitor: { forSeconds: 900, checks: [httpCheck(url, { expect: 200 }), …], onBreach: "rollback" }`.
  Checks are typed library pieces (HTTP status/body, later metrics queries), recorded in the manifest
  like other step config.
- After the step succeeds, the service runs the checks for the window. The step's attempt shows
  "monitoring"; a later stage can wait for the window to pass (prod after a quiet beta).
- On breach: rollback reruns the same deploy step with the previous healthy deployment's input (from
  the run that last passed its window on that step), then notifies through `onFailure`. Builds on
  environments (v2.2), which record what is deployed where.

### Road to prod (four instances, then cut over)

v1 prod/beta keep running beside v2 prod/beta until every pipeline has moved.

1. **v2 prod instance**: release `olve-pipelines-v2` in `apps`, runners in `olve-runners` (shared with
   v1 prod), own Garage, `pipelines-v2-private.ovea.pro`. **Live 2026-10-22**:
   `helm/values-v2-prod.yaml` (own Garage + bucket `olve-pipelines-v2`, 100Gi on `bulk`;
   `Webhooks__PublicBaseUrl: ""`; `Links__BaseUrl`; `Telemetry__ServiceName: olve-pipelines-v2`;
   `slo.enabled: false`, no sloth CRD), the Olve.Homelab route (Tailscale-private, no hook route) and
   the Authentik prod frontend redirect URI. No manual prerequisite: every referenced Secret
   (`olve-pipelines-minio`, `olve-pipelines-oidc`, `authentik-oidc-secrets`) and the `authentik-ca`
   ConfigMap already exist in `apps`, and Garage creates its key and bucket on first boot
   (`--default-bucket`, credentials from `olve-pipelines-minio`). Retention stays in dry run. Its
   Job sweep only selects Jobs with a v2 `olve-role` and one of this instance's pipelines, so v1
   prod's Jobs are safe. The Secret sweep is matched by name only (`olve-urls-<attempt>`,
   `olve-{map,fh,build}-…-urls`) and isn't instance-scoped, but v1 creates no such Secrets, and it
   deletes one only when its Job is gone or finished and it's over 1 h old. Check the first dry-run
   sweep logs (can v2 prod's OpenBao role list Secrets in `olve-runners`?) before
   `Retention__DryRun: "false"`.
2. **`deploy-prod`**: first stage of the pipeline's `when deployTo: "prod"` block, before
   publish-cli/publish-sdk (**done 2026-10-22**). Since 2026-10-22 every push goes all the way:
   deploy-beta → `beta-test` (smoke test of the fresh beta, in-cluster: `/api/ready`, `/api/pipelines`,
   the frontend bundle, a `problem+json` 404, SSE `hello`, `/install.sh`) → deploy-prod → publish-cli
   ∥ publish-sdk. A failed beta-test fails the run before prod. No manual promotion (user: aim for
   zero manual actions); `deploy-prod` is blocked only in special circumstances. `deployTo: "beta"`
   stops after beta; such a run can go to prod later with `pl run continue … --field deployTo=prod`.
   **The deployer moved to v2 prod early** (2026-10-22, before any other pipeline): "Olve.Pipelines
   v2" was recreated on prod (`6a395b72-3bb3-42a7-b01a-e28a6f138032`, same binding and secrets,
   `GITHUB_TOKEN` as credentials key) and its run #1 was the first prod deploy from it; `deploy-prod` redeploys
   prod itself (the executor reattaches after the restart), and prod's `/download` serves its
   `publish-cli`. The v2 beta copy (`f26d19cf…`) is paused (both deploy gates blocked) until the user deletes it;
   v2 beta keeps the fixture. Open: the first retention dry-run sweep logs on prod (above).
3. **Migrate pipelines** one repo at a time to v2 prod. One deployer per repo at a time: block the v1
   pipeline's deploy steps before binding v2. Recipe: getting-started "Starting from the reference
   pipeline", plus, before binding: pause v1 by blocking its deploying processing steps with v1's `pl`
   (`pl processing list <v1 pipeline>`, `pl processing block <step id>`; `config.yaml` and the v1
   pipeline stay as the rollback path, unblocking is the rollback), and copy secret values from v1's
   Secret by piping bytes: `kubectl -n olve-runners get secret olve-pipeline-<v1 id without dashes>
   -o jsonpath='{.data.KEY}' | base64 -d | pl secret set <pipeline> KEY`. Check the copy by comparing the sha256 of both Secrets' decoded values. v1 keeps running its
   production steps on every push until its pipeline is deleted; with its deploys blocked they deploy
   nothing.
   - **Migrated: Olve.Template.Api** (2026-10-22), the reference app pipeline other repos copy.
     Pipeline `cc720357-1994-45f5-95a1-52e482009e17` on v2 prod (`main`, `.pipelines`, SDK
     `2.0.0-beta.1` from npm, `GITHUB_TOKEN` as credentials key); run #1 (push) went
     source ∥ mirror-base-images → build ∥ test (`mise run ci`) → deploy-beta → beta-test →
     deploy-prod → prod-test, green in 5m07s. `beta-test` runs the whole API test suite against the
     fresh beta, like v1's `test-after-beta`: `mise run api:remote` in the dotnet SDK image with
     `API_BASE_URL` = the in-cluster Service derived from `HelmRelease`, after the `/health` wait and
     an SPA bundle check. Authenticated tests use a real beta Authentik token (`API_TOKEN`, else the
     suite mints HS256 tokens, which beta can't accept: `Auth:SigningKey` replaces Authentik
     validation): `beta-test` gets one by client credentials from the beta-only machine client
     `olve-template-api-tests` (Olve.Authentik blueprint; Authentik-generated secret held as the
     pipeline secret `API_TEST_CLIENT_SECRET`, declared on `beta-test` only), whose requested
     `olve-template-api-aud` scope rewrites `iss`/`aud` to the API's, like `questionbank-smoke`.
     Run #3: 11 tests, 11 passed, 0 skipped (1m07s); message tests delete what they create.
     `prod-test` is a read-only smoke test
     (`handlers/lib/smoke.ts`: `/health`, `/api/messages` page shape, `/api/auth-config`, the SPA
     bundle, an anonymous POST answering 401), since the suite writes data. No library steps added: the mirror step is a
     copy of this repo's (its own FROM list, the shared `olve-pipelines-v2/mirror` prefix, no
     `cacheRepo`), a candidate for a library step now that two pipelines use it. v1
     (`25ce1c9c…`) is paused: `deploy-beta` and `deploy` gates blocked, `config.yaml` kept; it
     still runs its build and check on every push. Gaps and gotchas: the commit that adds
     `.pipelines/` only seeds the push cursor (the first run came from the next commit, no manual
     trigger); `prod-test` needs a mapper (`from["deploy-prod"]`) because two steps produce
     `HelmRelease`; v1's `aoe-triage` failure handler has no v2 equivalent yet.
   - **Migrated: homelab-dashboard** (2026-10-22). Pipeline `e737bb35-031b-4209-9bd0-d1bc877ee2fe`
     on v2 prod (`main`, `.pipelines`, `GITHUB_TOKEN` as credentials key, passed to `binding create`
     directly); run #1 (push) green in 1m06s. Differs from Template.Api: v1 deployed straight to
     prod, so beta is new: a `homelab-dashboard` release in `apps-beta` with
     `set: { "ingress.enabled": "false" }` (no DNS record or certificate), tested through its
     Service. There is no test suite (a static page): `check` (beside `build`) verifies every local
     link in `index.html` exists and runs `helm lint --strict` with the beta and prod settings;
     `beta-test` checks beta serves this commit's `index.html` and every file it links byte for
     byte against the source tree, which proves the deployed image is this commit's; `prod-test`
     does the same against prod's Service and then through the ingress at its real hostname
     (`curl --resolve home-private.ovea.pro:443:192.168.11.8`, TLS verified: v1's `verify`;
     step pods reach traefik through the `runner-jobs-lockdown` policy). `prod-test` maps both inputs
     (`{ source: from.source, release: from["deploy-prod"] }`). `mirror-base-images` was then in three
     pipelines (copied again, `nginx:1.29-alpine` only); the pull-through caches replaced it. v1 (`50282eaa…`)
     was paused (gates blocked), then deleted by the user; `config.yaml` kept, its Secret left in place.
   - **Migrated: QuestionBank** (2026-10-22). Pipeline `ea076c20-5efc-4066-a8d4-c32571eebb3f` on
     v2 prod (`master`, `.pipelines`, SDK `2.0.0-beta.1` with the pull-through caches as `registryMap`);
     run #1 (push) green in 15m49s, 14 steps: source → build ∥ build-figures ∥ test ∥ beta-secrets →
     deploy-figures-beta ∥ deploy-beta → beta-test → prod-secrets → deploy-figures-prod ∥ deploy-prod →
     prod-test ∥ deploy-demo → demo-test. New vs Template.Api: (1) **a second, 3.5 GB image** (the
     Python figure service: torch, baked model weights). It can't go through `imageBuild`: the step
     pod's `/olve` emptyDir is capped at `Harness:ScratchSizeLimit` (1Gi; enforced under gVisor, the
     pod is evicted), and the tar is held twice plus `result.tar.gz`, then once more in Garage per run;
     also `imageBuild` can only build `<tree>/Dockerfile` with the root as context. So `build-figures`
     is a custom kaniko step that pushes to `registry.ovea.pro/questionbank/figure-service:src-<hash>`,
     the tag being the sha256 of `ctx.output.dir(services/figure-service)` (a deterministic tar), and
     skips kaniko when a manifest HEAD finds that tag (11 min when it builds, seconds otherwise;
     unchanged since June). `deploy-figures-*` are custom: `nerdctl pull` on the node through the k3s
     socket, helm with `pullPolicy=IfNotPresent`, a 300 s rollout. The QB image (125 MB) stays on
     `imageBuild` + `helmDeployOverSsh`. (2) **Secrets written into app namespaces**
     (`figure-service-auth`, prod's `questionbank-secrets`) by `beta-secrets`/`prod-secrets`, the value
     piped over ssh stdin to `kubectl create secret --from-file=k=/dev/stdin | kubectl apply
     --server-side` (v1 put it on the command line, and client-side apply copies it into
     `last-applied-configuration`). (3) The public **demo** is a third `helmDeployOverSsh`
     (`questionbank-demo`, `values-demo.yaml`; the image repository comes from `BuiltImage`, so v1's
     workaround is gone); `demo-test` checks demo mode, the SPA and that the Deployment's env has no
     credential names (ssh). `beta-test` runs the repo's `npm run test:beta` (8 passed, 0 skipped; it
     mints its own token from the existing `questionbank-smoke` client with the `questionbank-aud`
     scope and deletes its books); `test` runs typecheck and `npm test` (326 passed; v1 ran the server
     suite only). Dropped v1 secrets: `QB_BETA_OIDC_CLIENT_ID` (hardcoded) and
     `QB_PROD_ALLOW_DEFAULT_CUSTOMER` (was `0`, the chart default; the server no longer reads it). v1
     (`3f92aa4d…`) is paused, all four processing gates blocked; its three production steps, including
     the heavy figures build, still run on every push. Follow-ups: a `context`/subdirectory option and
     a registry-push mode for `imageBuild`; drop the `registryMap` once on beta.2.
   - **Migrated: Olve.SlimeRepublics** (2026-10-22). Pipeline `0e311e2d-de93-4677-a4af-c2d58009a6a4` on
     v2 prod (`main`, `.pipelines`, SDK `2.0.0-beta.2`, no `registryMap`); run #1 (push) green in 8m49s:
     source → web-source → build-server ∥ build-web ∥ test → deploy-beta → beta-test → deploy-prod →
     prod-test (build-server 5m58s: vcpkg and the C++ build from scratch, Catch2 inside the image build).
     New: (1) **two images in one chart.** The web image's context is `src/frontend`, so a 7 s
     `web-source` step outputs a `Source` whose `tree` is `ctx.output.dir(<tree>/src/frontend)` and a
     second `imageBuild` builds it, which keeps the cache mapping, stall watchdog and retries (until
     `imageBuild` gets a context option). `helmDeployOverSsh` takes one image and waits on
     `deploy/<release>`, but the chart has `<release>-server` and `<release>-web`, so `deploy-*` are
     custom (`handlers/lib/deploy.ts`: load both tars, `image.*` and `web.image.*` with
     `pullPolicy=Never`, wait on both rollouts). Two `Source` and two `BuiltImage` producers mean every
     step after `web-source` needs a mapper. (2) **The app's NetworkPolicy admits only Traefik** to the
     web pod, so the tests can't use the in-cluster Service: `beta-test` goes through
     `slimes-beta.ovea.pro` (runner policy 3b) and `prod-test` through public `slimes.ovea.pro`.
     `beta-test` is the repo's deployed-instance suite, the bot smoke test (`npm run smoke`: 3 bots
     for 60 s, PASS, 20 snapshots/s), after a socket retry and a client/bundle check; the game has its
     own login, so no Authentik client. `prod-test`: the client, its bundle, and one socket until the
     first server message (it briefly spawns a slime). `test`: Biome, Vitest (36 passed), typecheck +
     build. v1 (`092ba34d…`) is paused, all three processing gates blocked; v1's `aoe-triage` handler
     was dropped. Gotcha: v2 prod's Kubernetes token (Authentik client credentials) answered
     `invalid_grant` for a few minutes around 15:55 (secret set and reconcile answered 503); it cleared
     with the next v2 prod rollout, and the failed reconcile had to be re-run by hand.
4. **Cut over**: point `pipelines-private.ovea.pro` and `pipelines-hooks.ovea.pro` at v2 prod, merge `v2`
   to `main`, remove the v1 releases.
5. **Kill v1** (with the user): uninstalling v1 prod removes its MinIO Deployment and Service; the PVC
   `olve-pipelines-minio-data` (`resource-policy: keep`) stays orphaned until deleted by hand. The
   `olve-pipelines-minio` Secret stays: it holds the Garage credentials.

## Milestones

| | Milestone | Needs |
|---|---|---|
| M1 | A TS pipeline from `.pipelines-test/` reconciles on v2 beta into a version and runs end to end (push → describe → stages → outputs in Garage) — **done 2026-10-21** (manual, push and poll runs on beta) | A1–A4, A7, B1–B7 |
| M2 | API + `pl` cover runs, outputs, reruns, switch; `pl dev` loops work (B8 API done) | B8, C1, C2 |
| M3 | Frontend renders the v2 model — **views built 2026-10-21** (see track D notes) | D |
| M4 | v2 deploys itself to beta from `pipeline.ts`; v1 deployer pipeline retired — **done 2026-10-21** (v1 deployer paused, see E2 notes) | A6, B9, E1, E2 |
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
