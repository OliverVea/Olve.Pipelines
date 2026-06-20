# Parallel production → first-processing-step promotion deadlock — requirement

**Date:** 2026-06-19
**Status:** Bug / requirement → ready for review
**Repo:** OliverVea/Olve.Pipelines
**Found in:** the `olve-trains` pipeline (pipeline `c7474356-2eb6-4392-ac83-8860cb288dde`), a
multi-platform game build with two parallel production steps (`build-linux`, `build-windows`).

## Summary

When a **production job group has more than one parallel step**, the bundle can be promoted into
the first processing step **twice**, and the two resulting processing jobs **mutually supersede
each other** — each is marked `Obsolete` pointing at the other. Both are terminal, neither runs,
and the pipeline silently stalls after production with no error surfaced. The first processing
step (here, the test gate) never executes, so nothing downstream of it ever runs.

This is the multi-step production case the docs' own example #4 (Olve.Trains: parallel
`build-windows` + `build-linux`) recommends — so the blessed multi-platform pattern is exactly
what triggers it.

## Observed behaviour (reproduced repeatedly)

A single production trigger with two parallel build steps produced, every time:

```
build (production)  -> done
build (production)  -> done
test  (processing)  -> obsolete   supersedingJobId = <the other test job>
test  (processing)  -> obsolete   supersedingJobId = <the other test job>
```

Concrete evidence from one run (bundle `42f5327c`, processing step `2ff3518c`):

| Job        | Status   | `supersedingJobId` | createdAt (UTC)            |
|------------|----------|--------------------|----------------------------|
| `5a09d2e0` | obsolete | `68c84b06`         | 08:51:32.328604            |
| `68c84b06` | obsolete | `5a09d2e0`         | 08:51:32.328610            |

The two jobs were created **~6 microseconds apart** and each names the **other** as its
superseding job — a supersession cycle. A second run reproduced it identically
(`75840fed` ⇄ `fd35460d`). `GET /api/pipelines/{id}/binding/status` stayed `result: Success`
with no `problems`, and the obsolete jobs return *"Logs not available"*, so nothing in the API
indicates the pipeline is wedged — it just looks idle.

**Workaround applied in olve-trains:** collapse the two parallel production steps into a single
`build` step that publishes both RIDs. With one production job the group completes once, one test
job is created, and the gate runs. This sidesteps the bug but gives up production parallelism.

## Root cause

Two compounding races, both in the job/group completion path:

1. **Group completion is not fire-once.**
   `JobGroupCompletionService.HandleJobUpdated`
   (`src/Olve.Pipelines/Jobs/JobGroupCompletionService.cs`) is invoked per job transition and
   guards on a check-then-act:
   ```csharp
   if (groupJobs.Any(j => !j.Status.IsTerminal()))
       return;
   ...
   jobEvents.OnGroupCompleted.Invoke(group.Id);   // line ~41
   ```
   When the two parallel production jobs reach `Done` **concurrently**, both invocations observe
   *all* group jobs terminal (each sees the other already transitioned) and **both** invoke
   `OnGroupCompleted`. `DownstreamTriggerService.HandleGroupCompleted` →
   `TriggerFirstProcessingStep` → `CreateProcessingJob`
   (`src/Olve.Pipelines/Jobs/DownstreamTriggerService.cs`) therefore runs twice, scheduling two
   processing jobs for the same first step + bundle.

2. **Supersession is not a total order.**
   `JobObsoletionService` (`src/Olve.Pipelines/Jobs/JobObsoletionService.cs`) marks the prior job
   for a `(pipeline, step)` key obsolete:
   ```csharp
   jobService.UpdateJob<Job>(existingJob.Id, j => j with { Status = new Obsolete(newJob.Id) });
   ```
   When the two jobs from (1) are created concurrently, each creation sees the *other* as the
   "existing" job and obsoletes it. The result is `A.superseding = B` **and** `B.superseding = A`
   (`JobStatus.Obsolete(Id<Job> SupersedingJobId)`, `JobStatus.cs:17`). There is no surviving
   "newest" job — latest-wins degenerates into mutual loss.

Either race alone is sufficient to cause a stall; together they produce the observed cycle.

## Requirements

1. **Exactly-once group completion.** A `JobGroup` must raise `OnGroupCompleted` (and
   `OnGroupFailed`) **at most once**, regardless of how many of its jobs reach a terminal state
   concurrently. Concurrent terminal transitions of the last N jobs in a group must collapse to a
   single completion event (e.g. an atomic compare-and-set of a per-group `completed` flag, or
   serialized handling per `JobGroupId`).

2. **Promote once per production group.** A production group must promote its bundle into the
   first processing step exactly once. Promotion is keyed on the **group/bundle**, not on
   individual production job completions.

3. **Supersession is a strict total order.** A job may only be obsoleted by a **strictly newer**
   job for the same `(pipeline, processingStep)` key, and the newest such job must **never** be
   obsoleted by an older one. Two jobs may never name each other as `supersedingJobId`. After any
   set of concurrent scheduds for one key, **exactly one** job is left non-terminal/runnable.

4. **No silent deadlock.** If a promotion/scheduling sequence ever leaves **zero** runnable jobs
   for a step that should be running (e.g. a detected supersession cycle), it must surface as a
   `problem` on `binding/status` (or an equivalent error state), not present as a green, idle
   pipeline.

5. **Multi-step production is supported.** With requirements 1–3 satisfied, a pipeline with N > 1
   parallel production steps must build all N in parallel, produce one bundle, and run the first
   processing step exactly once — i.e. the docs' example #4 shape must work without the
   single-step workaround.

## Acceptance test

A pipeline with two (ideally three) parallel production steps, triggered once, results in:
exactly one `OnGroupCompleted`, exactly one processing job for the first step, that job runs to
`Done`/`Failed` (not `Obsolete`), and the gate chain proceeds. Run it repeatedly (the bug is a
race — single runs can pass by luck) and assert no `Obsolete` first-step jobs appear. A
deterministic unit test can drive two production jobs to `Done` on two threads through
`JobGroupCompletionService.HandleJobUpdated` and assert a single `OnGroupCompleted` invocation.

## Scope

- In: the group-completion fire-once guard, group-keyed promotion, total-order supersession, and
  the deadlock-visibility surfacing.
- Out: matrix/fan-out for production steps (separate concern); the promotion **gate** (brake)
  semantics are unaffected.

## Impact

Any pipeline with more than one production step is affected — silently. The recommended
multi-platform build pattern is currently broken; the only safe shape today is a single
production step.
