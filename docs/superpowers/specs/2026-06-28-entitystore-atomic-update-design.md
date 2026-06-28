# Atomic `EntityStore<T>.Update` — fix the read-modify-write data races — handoff

**Date:** 2026-06-28
**Status:** Design → ready to implement
**Repo:** OliverVea/Olve.Pipelines
**Parent:** [architecture review findings](2026-06-28-architecture-review-findings.md) issue #1
**For:** a fresh agent picking this up with no conversation history.

## Problem

Several services do a non-atomic read-modify-write against `EntityStore<T>`: `TryGet` a snapshot,
build a mutated copy, `Set` it back. Two threads interleaving lose one update.

- **`ArtifactBundleService.UpdateStatus`** (`src/Olve.Pipelines/Pipelines/Building/ArtifactBundleService.cs:30`)
  ```csharp
  public void UpdateStatus(Id<ArtifactBundle> id, ArtifactBundleStatus status)
  {
      if (_store.TryGet(id, out var bundle))
          _store.Set(bundle with { Status = status });   // <-- lost update under concurrency
  }
  ```
  Called from `JobGroupCompletionService.cs:45` (→ `Completed`) and `:53` (→ `Failed`). With the
  fire-once completion guard the bundle is now written once per group, but the primitive is still
  racy and other call paths (or future ones) can collide. Also returns `void` — inconsistent with
  the Result convention; callers can't tell "not found" from "done".
- **`JobService.UpdateJob<T>`** (`src/Olve.Pipelines/Jobs/JobService.cs:188`) — `TryGetJob` then
  `store.Set(updatedJob)`. Same race. This is the hot path: obsoletion, completion, status
  transitions all funnel through it.
- **`JobService.CancelJob`** (`src/Olve.Pipelines/Jobs/JobService.cs:206`) — `TryGet` then `Set`.

This is the same bug class as the parallel-production supersession deadlock fixed in commit
`673261d` — that fix removed the *symptom* paths (fire-once + total-order); this removes the
*underlying* lost-update primitive.

## The fix

Add an atomic compare-and-swap update to `EntityStore<T>` and route the three call sites through it.

### 1. `EntityStore<T>.Update` (new) — `src/Olve.Pipelines/Shared/EntityStore.cs`

```csharp
/// <summary>
/// Atomically read-modify-write the entity with <paramref name="id"/>. <paramref name="mutate"/>
/// may run more than once if it loses a CAS race; it must be a pure function of its input. Fires
/// <see cref="OnUpdated"/> exactly once on a real change, never on a no-op or a missing entity.
/// Returns false if the entity does not exist.
///
/// MUST NOT change a value that any index keys on — indexes deliberately do not subscribe to
/// OnUpdated (EntityStoreIndex.cs:20, EntityStoreUniqueIndex.cs:11). All current callers change
/// status only, never PipelineId/JobGroupId, so this holds.
/// </summary>
public bool Update(Id<T> id, Func<T, T> mutate)
{
    while (_entities.TryGetValue(id, out var current))
    {
        var updated = mutate(current);
        if (EqualityComparer<T>.Default.Equals(updated, current))
            return true;                       // no-op: present but unchanged, do not fire
        if (_entities.TryUpdate(id, updated, current))
        {
            OnUpdated.Invoke(id);
            return true;
        }
        // lost the race; another writer moved it — re-read and retry
    }
    return false;                              // not found
}
```

Notes:
- `_entities` is a `ConcurrentDictionary<Id<T>, T>`; `TryUpdate(key, newValue, comparisonValue)`
  is the CAS. Entities are records ⇒ value equality, so `comparisonValue: current` succeeds only
  while the stored value still equals the snapshot we mutated from. Correct CAS semantics.
- Keep the existing `Set` for create/replace. `Update` is for in-place mutation.

### 2. `ArtifactBundleService.UpdateStatus` → return `Result`, use `Update`

```csharp
public Result UpdateStatus(Id<ArtifactBundle> id, ArtifactBundleStatus status)
    => _store.Update(id, b => b with { Status = status })
        ? Result.Success()
        : new ResultProblem("Artifact bundle '{0}' not found.", id);
```
Update the two callers in `JobGroupCompletionService.cs:45,53`. They run inside an event handler
returning `void`; log a not-found via `logger.LogProblems(...)` rather than ignoring — a missing
bundle there is a real inconsistency worth a warning. (Don't fail the handler over it.)

### 3. `JobService.UpdateJob<T>` and `CancelJob` → use `Update`

`UpdateJob<T>` must preserve its current contract (typed, no-op short-circuit, "not found"
problem). Reuse the primitive while keeping the `T` type guard:

```csharp
public Result UpdateJob<T>(Id<Job> jobId, Func<T, T> update) where T : Job
{
    if (!TryGetJob<T>(jobId, out _))
        return new ResultProblem("Job with id '{0}' not found.", jobId);

    store.Update(jobId, j => j is T typed ? update(typed) : j);   // mutate runs under CAS
    return Result.Success();
}
```
For `CancelJob`, fold the `Scheduled`/`InProgress` → `Cancelled` switch into the `mutate` lambda;
preserve the "cannot be cancelled because it is {state}" problem. Because `mutate` may re-run on a
CAS retry, the switch (a pure function of the current job) is safe. To still return the
*can't-cancel* problem you need the post-state; read it back via `TryGet` after `Update`, or have
`Update` short-circuit (mutate returns the same instance → no-op → no event) and detect "unchanged
but present" separately. Simplest: keep a pre-check `TryGet` for the can't-cancel message, then
`Update` for the write — the pre-check is advisory, the `Update` is the atomic part.

## Tests — `test/Olve.Pipelines.UnitTests/`

Mirror the style of `JobObsoletionServiceTests` (manual wiring, `MonotonicTimeProvider`, looped
concurrency). Add an `EntityStoreTests` (none exists today):
1. `Update_Mutates_FiresOnUpdatedOnce` — subscribe a counter, assert one fire on a real change.
2. `Update_NoOp_DoesNotFire` — mutate returns an equal record ⇒ counter stays 0, returns true.
3. `Update_Missing_ReturnsFalse_NoFire`.
4. `Update_ConcurrentIncrements_NoLostUpdates` — N threads each `Update` a numeric field via
   `+1`; after a barrier-synchronized burst (loop ~200×), assert the final value == N. This is the
   regression test for the lost-update bug; it fails against the old `TryGet`+`Set`.
5. `ArtifactBundleService.UpdateStatus` — returns failure on unknown id; succeeds + flips status on
   a known id.

## Constraints / gotchas

- **Do not** use `Update` to change an indexed key (PipelineId on bundles/jobs, JobGroupId). The
  indexes (`EntityStoreIndex`, `EntityStoreUniqueIndex`) only track `OnAdded`/`OnDeleted` by design.
  If a future caller needs to change a key, it must `Delete`+`Set`, or the index must start handling
  `OnUpdated` — out of scope here.
- `mutate` runs under a possible retry loop ⇒ keep it pure, no side effects, no logging inside it.
- `EntityStore.Set`'s own add-vs-update detection (`EntityStore.cs:22`) is a separate, lower-impact
  check-then-act (can fire the wrong event *type* under a concurrent first-write, never corrupts
  data). Leave it; note it but don't expand scope.

## Acceptance

- `dotnet build` clean; `dotnet test --project test/Olve.Pipelines.UnitTests/...` green.
- New `Update_ConcurrentIncrements_NoLostUpdates` passes reliably across repeated runs.
- `ArtifactBundleService.UpdateStatus` returns `Result`; both call sites updated.
- `JobService.UpdateJob`/`CancelJob` route writes through `Update`; existing job tests stay green.
- Commit + push to `main` per repo convention (this auto-deploys to prod — it's a safe, tested
  internal refactor with no API/behaviour change beyond `UpdateStatus`'s new return type).

## Out of scope

Findings #2–#6 in the parent doc. Notably the `AttachmentStore` atomic compare-and-set/clear that
the manual-approval-gate spec needs is a **sibling** of this primitive — same pattern, different
store. Worth doing right after, but separately.
