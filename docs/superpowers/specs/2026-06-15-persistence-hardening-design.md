# Persistence hardening — design spec

**Date:** 2026-06-15
**Status:** Design draft → ready for review
**Repo:** OliverVea/Olve.Pipelines
**Priority:** Urgent — latent data-loss bug in production today.

## Goal

Make the S3-snapshot persistence layer **never overwrite good state with empty**,
and **never serve reads before state is loaded**. This is the gating prerequisite
for `pl` / self-installation
([2026-06-15-pl-cli-self-installation-design.md](2026-06-15-pl-cli-self-installation-design.md))
and stands on its own as a fix for a real data-loss footgun — independent of both
the typed-templates and self-install work.

## The bug (concrete)

`ConfigurationPersistenceService.StartingAsync`
(`src/Olve.Pipelines/Shared/Persistence/ConfigurationPersistenceService.cs:65-74`):

```csharp
catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
{
    logger.LogInformation("No existing configuration found in S3, starting fresh");
}
catch (Exception ex)                                  // transient / auth / parse
{
    logger.LogWarning(ex, "Failed to load configuration from S3, starting fresh");
}

await SaveAsync(cancellationToken);                   // <-- runs even after a FAILED load
```

A **non-404** failure (network blip, MinIO/STS auth failure, a corrupt/partial
JSON that fails to deserialize) is caught, the in-memory stores stay **empty**, and
control falls through to an **unconditional `SaveAsync`** that serializes those
empty stores and `PutObject`s them over the good `configuration.json`. **The good
state is gone.**

This is exactly the 2026-06-14 outage path; data loss was dodged **only because the
*save* also failed** on the same STS error. It will not be dodged next time (e.g. a
parse error, or storage that can read-fail but write-succeed).

`JobPersistenceService.StartingAsync`
(`.../JobPersistenceService.cs:56-65`) has the **identical** shape and the same bug.
Both must be fixed.

Compounding it: `/api/health` (`src/Olve.Pipelines/Health/HealthEndpoints.cs:7`)
returns `Ok()` unconditionally — no gate on load — so the API serves `200` with an
**empty pipeline list** during/after a failed load (what the outage looked like
externally).

## Three failure modes to distinguish

The catch-all conflates these; the fix must separate them:

| Case | Signal | Correct behaviour |
|---|---|---|
| **Genuinely empty / first run** | `404 / NoSuchKey`, or empty body → `null` snapshot | Start fresh; writing an empty baseline is **safe** (we *know* nothing exists). |
| **Load failed (transient/auth)** | any other S3/network exception | **Do not save.** Fail startup; retry. |
| **Load failed (corrupt/parse)** | `JsonException` / deserialize failure | **Do not save.** Fail startup; surface loudly (needs a human / restore). |

Only the first is "start fresh." The other two must **never** reach `SaveAsync`.

## Operating modes — ephemeral vs persistent

The app must support **launching with no persisted storage** (local dev, tests,
throwaway runs): it comes up, is ready, serves, and simply never reads or writes a
snapshot. This is a **first-class supported mode**, not a degraded/failure state.

Two explicit modes:

- **Ephemeral** — no snapshot load, no save, **ready immediately**. In-memory only;
  state is lost on restart, by design.
- **Persistent** — storage configured; the hardening rules below apply (must load
  before ready, crashloop on load failure, never save empty).

Today the mode is **implicit**: `s3 is null` ⇒ the services skip load/save
(`ConfigurationPersistenceService.cs:33-37,126-130`). That's the right behaviour but
the wrong trigger — a prod deploy that *loses* its storage config would silently run
ephemeral and never persist. So:

- **Make the mode explicit, persistent-by-default.** Ephemeral requires an explicit
  opt-in (e.g. `Storage:Mode=Ephemeral`, or a `--ephemeral` / launch-profile flag);
  absence of storage config in a non-ephemeral run is a **startup failure**, not a
  silent in-memory fallback. A misconfigured prod fails loud; local dev opts in once.
- The crashloop-on-load-failure (Design §3) and the load-gated readiness (§5) apply
  **only in persistent mode**. In ephemeral mode the readiness signal is satisfied
  with no load, and `SaveAsync` is a no-op (the `_loaded` gate is effectively "no
  store").
- *Decision P3:* opt-in mechanism — config key `Storage:Mode` (recommended; visible,
  testable, one knob) vs a launch-profile/env flag. Local dev sets it in
  `appsettings.Development` / user-secrets / launch profile.

## Design

Applies to both `ConfigurationPersistenceService` and `JobPersistenceService`
(the snapshot-based stores). `BundlePersistenceService` / `S3BundleStore` are
content-addressed object storage, not whole-state snapshots — out of scope, but
audit for the same "catch → write" shape.

1. **Classify, don't catch-all.**
   - `404` / `null` snapshot → `FirstRun`.
   - `JsonException` → `Corrupt` (terminal).
   - anything else → `LoadFailed` (transient).

2. **Never save after a failed load.** Remove the unconditional post-load
   `SaveAsync`. Save an empty baseline **only** on the `FirstRun` path. On success
   there's nothing to write back (drop the redundant startup save entirely).

3. **Fail startup hard on `LoadFailed` / `Corrupt`.** Throw from `StartingAsync`
   (or return a failure the host treats as fatal) so the pod **crashloops and
   retries** — a self-healing loop for the transient case, and a loud, non-serving
   failure for the corrupt case. The platform (k8s restart/backoff) is the retry
   mechanism; no bespoke in-process retry loop needed.
   - *Decision P1:* crashloop-via-throw (recommended, simplest, uses k8s backoff)
     vs in-process retry-with-backoff that defers readiness. Leaning crashloop.

4. **A `_loaded` write-gate (belt-and-suspenders).** `RequestSave` / `SaveAsync`
   no-op until a load has been **confirmed** (`FirstRun` or success). Even if a
   future code path tries to save before load, it cannot write empty. Pairs with
   the existing `_loading` guard.

5. **Readiness gate.** Add a **readiness** signal, distinct from liveness, that is
   not ready until every snapshot store has confirmed load. The data API must not
   serve reads before then.
   - Liveness (`/api/health`): process is up (keep as-is).
   - Readiness (`/api/ready`, new): all persistence services loaded. K8s routes
     traffic only when ready; a stuck load keeps the pod out of the Service rather
     than serving `[]`.
   - *Decision P2:* a shared `IPersistenceReadiness` the services flip on confirmed
     load, aggregated by the readiness endpoint. (Fits the existing
     `IRunOnStartup` / `StartupRunner` pattern.)

## Scope

In v1:

- **Explicit ephemeral vs persistent mode**, persistent-by-default; ephemeral is an
  opt-in for local/test runs with no storage. Missing storage config in a
  non-ephemeral run fails startup (no silent in-memory fallback).
- Failure-mode classification + "save only on confirmed first-run" in both snapshot
  persistence services.
- Remove the unconditional post-load save.
- Hard-fail startup on transient/corrupt load.
- `_loaded` write-gate.
- Readiness endpoint gated on confirmed load; helm chart wired to use it.

Out of v1:

- Private MinIO / bundled deps (removes the *cause* class — `pl` spec — but
  hardening is needed regardless of where the store lives).
- Snapshot backups / versioning / point-in-time restore (worth a follow-up; a
  write-gate prevents *creating* the empty-overwrite, it doesn't recover a prior
  bad one).
- Optimistic-concurrency / multi-writer protection (single-replica controller; note
  the two-writer risk from running the local app against beta, but don't solve it
  here).

## Tests

- **Regression (the bug):** load throws a non-404 → `SaveAsync` is **not** called
  and S3 still holds the original snapshot. (Reproduces the overwrite against
  pre-fix code; passes after.)
- Corrupt JSON → startup fails, no save, store untouched.
- `404` / null snapshot → starts fresh, empty baseline saved, ready.
- Successful load → stores populated, **no** redundant startup save, ready.
- Readiness is **not ready** until load confirmed; flips ready after.
- **Ephemeral mode:** launches with no storage, **ready immediately**, `SaveAsync`
  never writes, no startup failure.
- **Persistent mode with no storage config:** startup **fails** (no silent
  ephemeral fallback).
- Same matrix for `JobPersistenceService`.

## Sequencing

Land this **before** anything that makes the controller system-of-record at scale
(`pl` Layer 1 reconcile). Independent of typed templates. Small, controller-side,
high urgency — do first.
