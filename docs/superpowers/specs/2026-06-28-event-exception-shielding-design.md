# Per-handler exception shielding in `Event<T>` — handoff

**Date:** 2026-06-28
**Status:** Design → ready to implement
**Repo:** OliverVea/Olve.Pipelines
**Parent:** [architecture review findings](2026-06-28-architecture-review-findings.md) issue #2
**Sibling done:** issue #1 — atomic `EntityStore.Mutate` (commit `a39f3b4`,
[design](2026-06-28-entitystore-atomic-update-design.md))
**For:** a fresh agent picking this up with no conversation history.

## Problem

`src/Olve.Pipelines/Shared/Event.cs` is the whole event primitive:

```csharp
public class Event<T>
{
    private Action<T>? _handlers;
    public void Invoke(T message) => _handlers?.Invoke(message);   // <-- no shielding
    public void Subscribe(Action<T> handler) => _handlers += handler;
    public void Unsubscribe(Action<T> handler) => _handlers -= handler;
}
```

`_handlers?.Invoke(message)` runs a multicast delegate. If **one** subscribed handler throws:

1. **Downstream handlers never run** — a multicast delegate stops at the first exception, so
   every handler registered *after* the thrower is silently skipped.
2. **The exception propagates back to whoever called `Invoke`.** Invoke is called synchronously
   from inside the store mutation primitives — `EntityStore.Set` (`EntityStore.cs:26,28`),
   the new `EntityStore.Mutate` (`EntityStore.cs:~48`), `EntityStore.Delete`, and
   `AttachmentStore.Set/Remove` (`AttachmentStore.cs:22,31`). So a throwing **domain** handler
   surfaces as a failure to the code that did the write — **even though the write already
   succeeded.** Write committed, caller sees an exception. That is the worst shape of failure.

### Why this matters here

The two-tier model wires it so the blast radius is real. Store events forward to domain hubs, and
the hubs fan out to the real handlers (`src/Olve.Pipelines/Jobs/JobEventRegistration.cs:14-22`):

```csharp
store.OnUpdated.Subscribe(events.OnUpdated.Invoke);                                  // tier 1: store -> hub
events.OnUpdated.Subscribe(id => sp.GetRequiredService<JobGroupCompletionService>()  // tier 2: hub -> domain
    .HandleJobUpdated(id));
```

So when `JobGroupCompletionService.HandleJobUpdated` (or any of the ~65 subscriptions —
`grep -rn "\.Subscribe(" src/Olve.Pipelines`) throws, the exception unwinds tier 2 → tier 1 →
`store.Set/Mutate` → the original mutator. And any *other* domain handler on the same event never
runs. This is the same "silent infra failure" family as the rest of the review: a single buggy
subscriber can both swallow its siblings and corrupt an unrelated caller's result.

This is a latent footgun, not a live incident — handlers are mostly well-behaved today. Fix it
before it bites.

## The fix

Make `Event<T>.Invoke` dispatch each handler in isolation: iterate `GetInvocationList()`, wrap each
call in try/catch, log, and continue. No handler can break a sibling or the writer.

### 1. `Event<T>.Invoke` — per-handler isolation

```csharp
public void Invoke(T message)
{
    var handlers = _handlers;          // snapshot: Subscribe/Unsubscribe replace the field
    if (handlers is null) return;

    foreach (var handler in handlers.GetInvocationList())
    {
        try
        {
            ((Action<T>)handler)(message);
        }
        catch (Exception ex)
        {
            EventDispatch.OnHandlerException?.Invoke(ex);   // see §2 — never rethrow
        }
    }
}
```

Semantics change deliberately: after this, `Invoke` **never throws**. Callers of `Set`/`Mutate`/
`Delete` are fully decoupled from handler failures (which is the point).

### 2. Logging delivery — the one real decision

`Event<T>` is a tiny primitive constructed via **parameterless `= new()`** everywhere — every hub
(`JobEvents.cs`, `PipelineEvents.cs`, `ProcessingStepEvents.cs`, …), `EntityStore` (3 events),
`AttachmentStore` (2). DI can't reach those construction sites, so there is no `ILogger` in scope.
Three ways to get the exception to a log:

- **(RECOMMENDED) Static non-generic sink.** A `static Action<Exception>? OnHandlerException` on a
  small non-generic `EventDispatch` class, set once at startup. Cheapest (zero construction-site
  churn — all `= new()` stay), uniform (every Event logs), and keeps the primitive
  **logging-agnostic** — the sink is `Action<Exception>`, no `Microsoft.Extensions.Logging`
  dependency, which matters because `Event<T>` is being moved into **Olve.Utilities** (see the
  `project_entitystore_to_olve_utilities` note — keep the package dependency-light). The finding
  itself calls this fix "cheap"; this keeps it cheap.

  ```csharp
  // src/Olve.Pipelines/Shared/EventDispatch.cs  (moves to Olve.Utilities alongside Event<T>)
  public static class EventDispatch
  {
      /// Process-wide sink for exceptions thrown by Event<T> handlers. Set once at startup.
      /// Null = swallow. Static because Event<T> is constructed inline (new()) where DI can't reach.
      public static Action<Exception>? OnHandlerException { get; set; }
  }
  ```

  Wire it in an `IRunOnStartup` (or `ServiceConfiguration`) from an injected
  `ILogger<...>`/`ILoggerFactory`:
  ```csharp
  EventDispatch.OnHandlerException = ex =>
      logger.LogError(ex, "Unhandled exception in an event handler; other handlers still ran.");
  ```
  Downside: global mutable state. Acceptable — it's write-once in prod and a single seam. For the
  one unit test that asserts delivery, set/reset it under `[NotInParallel]` (TUnit) so parallel
  tests don't clobber the static.

- **(Alternative) Instance error-sink via constructor.** `Event<T>(Action<Exception>? onError = null)`,
  forwarded from `EntityStore`/`AttachmentStore`/each hub. More "explicit wiring" (the house ethos),
  no statics, instance-isolated in tests — but every `= new()` site (≈15) must thread a sink, and
  `EntityStore`/`AttachmentStore` (generic, also migrating) must gain an optional sink param. Higher
  churn for the same outcome. Pick this only if Oliver wants to avoid the static on principle.

- **(Rejected) Mandatory `ILogger` on `Event<T>`.** Couples the primitive to MEL, breaks every
  manual-wiring unit test, and fights the Olve.Utilities move. Don't.

**Default to the static sink** unless told otherwise; note the choice in the commit message.

## Tests — `test/Olve.Pipelines.UnitTests/`

Add `EventTests` (none exists). Manual wiring, no DI container (house style — see
`JobObsoletionServiceTests`).

1. `Invoke_OneHandlerThrows_LaterHandlersStillRun` — subscribe handler A (sets flag), B (throws),
   C (sets flag); after `Invoke`, assert A **and** C ran. (Fails against the old multicast `Invoke`,
   where C is skipped — this is the regression test.)
2. `Invoke_HandlerThrows_DoesNotPropagate` — `Invoke` of a throwing handler does not throw.
3. `Invoke_HandlerThrows_ForwardsToSink` — set `EventDispatch.OnHandlerException` to capture; assert
   it received the exception. Mark `[NotInParallel]` and reset the static in a finally.
4. `Invoke_NoHandlers_NoOp` — `Invoke` with nothing subscribed does not throw.
5. `EntityStore_Set_HandlerThrows_WriteSucceeds_CallerUnaffected` — subscribe a throwing handler to
   `OnUpdated`, `Set` an existing entity; assert `Set` returns normally and `TryGet` shows the new
   value. Ties the fix back to the finding-#1 surface (the store write must not be poisoned by a
   subscriber).

## Constraints / gotchas

- **Snapshot `_handlers` into a local before iterating.** `Subscribe`/`Unsubscribe` use `+=`/`-=`,
  which replace the field with a new delegate; iterating a captured snapshot is safe against a
  concurrent (un)subscribe and against a handler that (un)subscribes mid-dispatch.
- **`Subscribe`/`Unsubscribe` remain non-atomic.** That is pre-existing and fine: subscription
  happens once at startup, single-threaded, in `*EventRegistration.Run`. Making them
  `Interlocked`-safe is **out of scope** — note it, don't do it.
- **Catch `Exception` broadly.** The whole point is total isolation; do not try to filter "fatal"
  types. (No special-casing `OperationCanceledException` — handlers here are sync side effects.)
- **Order is preserved.** `GetInvocationList()` returns handlers in subscription order, same as the
  multicast invoke — only the failure behavior changes.
- **Reentrancy is unchanged.** Handlers still run synchronously on the invoking thread; a handler
  may still mutate a store and trigger nested `Invoke`s. The try/catch doesn't alter that.

## Acceptance

- `dotnet build` clean (warnings-as-errors is on); `dotnet test` green.
- New `EventTests` pass, including `Invoke_OneHandlerThrows_LaterHandlersStillRun` reliably.
- A throwing handler no longer (a) skips sibling handlers or (b) surfaces to the `Set`/`Mutate`
  caller; it is logged via the sink instead.
- Commit + push to `main` per repo convention (auto-deploys to prod; behaviour-only hardening with
  no API change — but confirm with Oliver before pushing, as #1 was held).

## Out of scope

- Findings #3–#6 (parent doc).
- Atomicity of `Subscribe`/`Unsubscribe` (startup-only; safe today).
- The `EntityStore.Set` event-type race (separate; deliberately not done with #1).
- The Olve.Utilities migration itself — but **implement `Event<T>` + `EventDispatch` so they port
  cleanly** (logging-agnostic, no new package deps), since they are moving there.
