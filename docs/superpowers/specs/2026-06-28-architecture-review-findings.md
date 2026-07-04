# Architecture review — findings & backlog

**Date:** 2026-06-28
**Scope:** Whole-app architecture review (5 parallel subsystem surveys: Shared infra, Pipelines
domain, DI/composition, persistence/K8s I/O, tests/error-handling).
**Verdict:** Strong (B+/A−). Patterns are real and uniform, no god-classes, disciplined
persistence, AOT-ready, Result-everywhere. The issues below are refinement-level, not structural.

## The through-line

Three of the top findings (#1, #3, and the deadlock spec's requirement #4) are the same theme:
**concurrent / infrastructure failures that manifest as a silent idle rather than a loud error.**
The app is excellent at the happy path and at structured error *returns*; its weak spot is making
*infrastructure* failures visible. If only one follow-on is taken, make it that.

## Issues (priority order)

### 1. Data race in `ArtifactBundleService.UpdateStatus` — lost updates  **[HIGH — real bug]**
`src/Olve.Pipelines/Pipelines/Building/ArtifactBundleService.cs:30` does read-then-`Set` with no
atomicity; called from `JobGroupCompletionService.cs:45,53` on concurrent job completions. Same
class as the just-fixed supersession deadlock. `JobService.UpdateJob` (`JobService.cs:188`) and
`CancelJob` (`JobService.cs:206`) are the same read-then-`Set` race. Also: `UpdateStatus` returns
`void`, inconsistent with the Result convention.
→ **Fix:** atomic `EntityStore<T>.Update(id, mutator)` CAS primitive; route the three call sites
through it. Lets the scattered defensive `TryGet`-after-index-lookup pattern stay correct.
→ **Handoff written:** [2026-06-28-entitystore-atomic-update-design.md](2026-06-28-entitystore-atomic-update-design.md)

### 2. `Event.Invoke` has no exception shielding  **[MEDIUM]**
`src/Olve.Pipelines/Shared/Event.cs:7` — one throwing handler stops all downstream handlers **and**
surfaces as a failure to the mutation caller, even though the `Set`/`Delete` write already
succeeded. Synchronous two-tier event model makes this a latent footgun.
→ **Fix:** wrap each handler dispatch in try/catch-log (per-handler isolation). Cheap.
→ **Handoff written:** [2026-06-28-event-exception-shielding-design.md](2026-06-28-event-exception-shielding-design.md)

### 3. K8s-unavailable job stall — infinite retry, no backoff, invisible  **[MEDIUM]** — ✅ DONE
`src/Olve.Pipelines/Jobs/KubernetesJobExecutor.cs` (~`SubmitOrReattachAsync`, line ~142): if
submission throws, the job stays `Scheduled` and `JobRunner` retries every 1s forever with no
backoff and nothing surfaced — presents as an idle pipeline. Same "silent wedge" family as the
deadlock spec's requirement #4 (surface a stuck/zero-runnable state instead of green-and-idle).
→ **Fixed:** the submission phase (per-job S3 secret + submit/reattach) is now wrapped in
`SubmitGuardedAsync`. A pre-`InProgress` failure is a submission stall: the attempt is counted on
`Scheduled(int Attempts)` (persisted, survives restart), a `controller: failed to submit…` line is
appended to the job's S3 run log, and the watcher holds its slot for a short `SubmissionRetryDelay`
before exiting (so `JobRunner`'s respawn rides out a transient outage instead of hammering K8s at
tick rate). After `MaxSubmissionAttempts` (3 = initial + 2 retries) the job is marked `Failed` with
a reason and the per-job S3 secret is cleaned up. A post-`InProgress` failure still rethrows →
reattach (unchanged). Tests: `SubmissionKeepsFailing_MarksFailedAfterMaxAttempts`,
`SubmissionFails_LeavesJobScheduledWithBumpedAttempts`,
`SubmissionFailsThenRecovers_CompletesWithoutFailing`.

### 4. `async void` timer ticks in persistence services  **[LOW–MEDIUM — fragility]**
`JobPersistenceService.cs:131`, `ConfigurationPersistenceService.cs:156`,
`PromotionPersistenceService.cs:130`. Currently safe (each `SaveAsync` self-catches) but fragile to
future edits.
→ **Fix:** `async Task` wrapper or periodic `BackgroundService`.

### 5. Trigger orchestration duplicated  **[MEDIUM — maintainability]**
`src/Olve.Pipelines/Pipelines/PipelineEndpoints.cs:45-73` inlines production bundle+group+job
creation instead of delegating to `TriggerExecutionService` — two sources of truth for "start a
production run." (The processing path is better: it funnels through `JobService.CreateProcessingRun`.)
Re-promote orchestration is similarly spread across `ProcessingStepEndpoints.cs:~100-125` and
`TriggerExecutionService`.
→ **Fix:** endpoints delegate; one execution path for every trigger entry point.

### 6. Test gaps on the silent-failure-prone paths  **[MEDIUM]**
Domain rules are well-covered (job scheduling, supersession, cascade — incl. the new 200× stress
tests). The untested code is exactly the I/O boundary where silent failure hurts:
- `PollTriggerService` (`src/Olve.Pipelines/Pipelines/Polling/PollTriggerService.cs:82-126`) — the
  background GitHub poller that fires deploys — has **zero tests**.
- `KubernetesJobExecutor` failure modes (API errors, timeouts, bad responses) — untested; its test
  also uses a DI container while every other unit test wires manually.
- `TriggerExecutionService` / `ReconcileCoordinator` error branches — only happy path + drain
  timeout covered.
→ **Fix:** add `PollTriggerService` unit tests, K8s-failure scenarios, a job-failure-cascade E2E.

## Minor / watch (no action yet)

- `AppJsonContext` is hand-maintained (~68 types); a new API response type added without a
  `[JsonSerializable]` entry fails at runtime under AOT. Consider a guard test.
- Hosted-service shutdown ordering (`ServiceConfiguration.cs:106-109`) is comment-driven —
  `JobWatcherRegistry` must register before `JobPersistenceService`. Correct but implicit/fragile.
- `S3BundleStore` `_bucketEnsured` is a non-volatile bool — safe by S3 idempotency, not by design.
- Orphaned K8s secrets if a pod crashes mid-watcher (cleanup only on terminal job state) — slow
  leak, UUID-named so low collision risk.
- Inconsistent startup failure modes: snapshot persistence fails hard on S3-down;
  `BundlePersistenceService.cs:28` soft-fails. Defensible (bundles are artifacts) but muddled.

## NOT an issue (verified false positive)

The DI survey flagged `services.AddTransient<IEnumerable<Job>>(_ => [])` etc. (`ServiceConfiguration.cs:80,83,85`)
as dead code. They are **load-bearing**: `EntityStore<T>`'s constructor takes
`IEnumerable<T> initialEntities`, so these seed the empty stores via DI. Do not remove.
