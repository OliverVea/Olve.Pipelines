# Olve.Pipelines v2 — implementation plan

Companion to [pipelines-v2.md](pipelines-v2.md) (the spec) and
[pipelines-v2-spikes.md](pipelines-v2-spikes.md) (spike findings). Work happens on the `v2` branch;
v2 beta (`pipelines-v2-beta.ovea.pro`) deploys itself from this branch's `.pipelines/pipeline.ts` since
milestone M4 (2026-10-21); the v1 pipeline "Olve.Pipelines v2" that ran `.pipelines/config.yaml` is
paused, not deleted — see the E2 notes for the rollback.

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
  (`pl gate block f26d19cf-810a-4fb8-b5af-e61ca73b1bae deploy-beta` against v2 beta):
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
    `sdkHash` note); the stall source itself (MCR idle connection; mirroring base images into
    registry.ovea.pro would remove it); the 10 s TERM→KILL grace is an SDK constant (the 300 s
    window is `stallTimeoutSeconds`).
- **Gaps** (as found on 2026-10-21; (1) and (2) fixed above): (1) the config cursor watches only `.pipelines/`, but `pipeline.ts` builds against
  `../sdk` — an SDK-only commit does not rebuild the step binary, and reconcile-now does not help (the
  build Job is per config commit, `olve-build-<p12>-<sha12>`, so it rebuilds the same old checkout).
  Proof: versions v1 (aeb5960) and v2 (69df341) differ only in `sdkHash` and handler hashes, i.e. runs
  #1–#3 ran the SDK from before b5b3f93. Only a `.pipelines/` commit picks up SDK changes. (2) **kaniko
  hangs at "Unpacking rootfs"**: 2 of 6 builds (#2, #4 — the first two push runs; push run #6 was fine); the stalled pod idles (3m
  CPU) with one ESTABLISHED, empty connection to 150.171.70.10:443 (Microsoft — the MCR base-image
  pull stalled and nothing times it out). Hypothesis (n=5): both hung builds started within ~2 min of
  v1's production build of the same commit (same base image, node, egress); the green ones started 6+
  min clear — if so, stopping v1's (now useless) production builds would remove it. v1 itself
  has had one 4120 s production job out of 41, so MCR stalls are not v2-only. Impact: one attempt per
  (pipeline, step) may be InProgress (`JobRunner`), so a hung build also holds the next push's build
  until `timeoutSeconds: 3600` expires. Recovery: `pl run cancel <p>#n && pl run rerun <p>#n`.
  Mitigations to decide: a build timeout near the normal ~5 min (e.g. 900 s), an inactivity
  watchdog/retry in `steps.imageBuild`, mirroring the base images in registry.ovea.pro, or stopping
  v1's production builds (e.g. deleting its production steps from the v1 pipeline — not done: the
  rollback relies on them). (3) A cancelled attempt can leave a `result.tar.gz` (retention's 24 h
  leftover sweep covers it once dry run is off).

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
