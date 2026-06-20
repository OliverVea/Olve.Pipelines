# Manual approval gate for processing steps — design

**Date:** 2026-06-20
**Status:** Design → ready for review
**Repo:** OliverVea/Olve.Pipelines
**Depends on:** [parallel-production promotion deadlock fix](2026-06-19-parallel-production-promotion-deadlock-design.md) — **hard prerequisite, see §Prerequisite.**

## Summary

A processing step can be marked a **manual gate**. When the cascade reaches it, the bundle is
**parked** — not run, not dropped — until an operator approves. Approval promotes the **latest**
bundle that reached the gate (not the one pending when the gate was first hit) and the chain
continues automatically. The gate **re-arms**: the next bundle parks again.

Use cases: manual visual-regression sign-off, manual prod-deploy approval.

## Today vs. wanted

The existing promotion gate (`ProcessingStepPromotion(bool Blocked)`) is a **brake**: a blocked
step **drops** the bundle on the floor (`DownstreamTriggerService.cs:50,87`); the operator must
unblock and manually `re-promote`. There is no wait-then-continue, and nothing remembers what was
trying to get through. A manual gate is a planned, recurring checkpoint that **remembers and
continues** — a distinct concept, layered alongside the brake, not replacing it.

## Two layers

| Concern | Owner | Storage | Reconciled? |
|---|---|---|---|
| **Gate mode** (`Auto`/`Manual`) | git (config) | field on `ProcessingStep`, like `Order` | yes |
| **Pending bundle** (parked release) | operator (ops) | `AttachmentStore<ProcessingStep, PendingApproval>` | **never** |
| **Brake** (`Blocked`) | operator (ops) | existing store, unchanged | no |

The requirement is config (it's part of *what the pipeline is*). The parked bundle is operational
state (the operator produces it by not-yet-approving). Same dividing line the brake already draws.

## Config layer

```yaml
processingSteps:
  - name: deploy-prod
    gate: manual          # new; default 'auto'. Sibling of `configuration`.
    configuration: { image: alpine:latest, scriptFile: scripts/deploy.sh }
```

- `ProcessingStep` gains `GateMode Gate` (`enum { Auto = 0, Manual }`; `Auto = 0` so pre-feature
  records and `default` are fail-safe — never park a release nobody is watching).
- `ProcessingStepDocument` gains `GateMode? Gate`.
- Reconcile mirrors `Order`: `ProcessingStepService.Create` takes `GateMode` (born correct); add
  `UpdateGate`; `PipelineReconciler` sets it on the create branch **and** diffs+updates on the
  match branch. Gate must read independently of `StepConfiguration` (which is blob-replaced).

## Ops layer

- `PendingApproval(Id<ArtifactBundle> BundleId, DateTimeOffset Since)` in a new attachment store.
  Absence = nothing parked. `Since` powers the UI's "awaiting approval for 3 days".
- Persisted by **extending `PromotionPersistenceService`** (one timer, one snapshot file, second
  array) — same write-gating discipline (never save before load confirmed; never overwrite good
  state with empty on a transient read failure) and a `JsonSerializerContext` entry (AOT). Losing
  a parked bundle silently loses a release, so this is *more* safety-critical than the brake.

## Cascade change

The one behavior change, in `DownstreamTriggerService` (`TriggerFirst` **and** `TriggerNext`):

```
if (step.Gate == Manual) {
    pendingApprovals.CompareAndSet(stepId, group.ArtifactBundleId, now);  // latest-wins, atomic
    return;   // park — do not run, do not drop, even if braked
}
if (promotionGate.IsBlocked(step.Id)) return;   // brake unchanged
```

Latest-wins on the slot **is** the "approve the latest" feature: each newer bundle reaching the
gate overwrites the slot. Park *always*, even while braked — otherwise unblocking has nothing to
approve and the release is lost.

## Actions (`/api/processing-steps/{stepId}/...`)

- **`approve`** — refuse if braked; read slot (empty → "nothing to approve"); guard the bundle
  still exists (`bundles.TryGet`, like re-promote `ProcessingStepEndpoints.cs:116`);
  `CreateProcessingRun(pipelineId, slot.BundleId, stepId)`; **compare-and-clear** the slot (clear
  only if still the bundle just read). Gate stays `Manual` (config) → next bundle re-parks = the
  recurring re-arm, for free.
- **`reject`** — `pendingApprovals.Remove(stepId)`. Nothing to obsolete (parking created no job).
- `approve` and `re-promote` share one tail (`config-exists → bundle-exists → CreateProcessingRun`)
  so they can't drift.

After approve, the run completes → `OnGroupCompleted` → `TriggerNext` advances the chain. If the
next step is also `Manual`, it parks there. Adjacent manual steps work; needs an integration test.

## Read / UI

Extend `StepPromotionState` (`ListProcessingStepPromotions`, `ProcessingStepEndpoints.cs:29`) to
`{ blocked, gate, pending: { bundleId, since } | null }` — one call already powers the flow strip.
UI shows "⏸ awaiting approval — bundle X, since T" + Approve/Reject by the existing brake controls,
and surfaces "N newer builds skipped" (see §Decisions).

## Concurrency requirements

The slot is touched by concurrent watcher threads (cascade `Set`) and the operator (`approve`
clear). `AttachmentStore.Set` / `EntityStore.Set` are non-atomic read-modify-write with no
compare primitive. Two named races, both of which silently drop a release:

1. **Lost park.** Approve reads B → newer B2 parks → approve's plain `Remove` nukes B2's slot. B2
   ran nowhere; its cascade already returned. → `approve` must **compare-and-clear** (clear only
   if slot still B); the parking write must be **compare-and-set** (overwrite only with a strictly
   newer bundle).
2. **Wrong bundle approved.** UI showed B, B2 overwrote the slot before the read, operator ships
   B2 thinking it's B. Acceptable under "approve the latest" (§Decisions) **iff** the UI
   live-updates the slot and the audit records the bundle actually promoted, not the one displayed.

→ `AttachmentStore` gains an atomic compare-and-set / compare-and-clear (or a small
`PendingApprovalService` owns it). This is a prerequisite for parking, not a detail.

## Prerequisite

The deadlock fix (exactly-once `OnGroupCompleted`) **is a doc, not code** —
`JobGroupCompletionService.HandleJobUpdated` is still unguarded check-then-act and statuses are
written by concurrent watcher threads, so `OnGroupCompleted` double-fires today.

- For **Auto** promotion the double-fire is the mutual-supersession deadlock (that spec's bug).
- For **Manual** parking the double-`Set` is idempotent (same bundle, no job created, no cycle) —
  so parking *looks* safe and tempts shipping early. It is not: mixed pipelines still deadlock, and
  the atomic-slot requirement above assumes the fire-once guard. **Land the deadlock fix first.**

## Adjacent edge cases

- **Rename = delete+recreate** (`PipelineReconciler.cs:146-150`, new `Id`). The attachment store's
  delete-subscription removes the slot → a parked approval **silently vanishes** on an unrelated
  YAML rename. Emit a `problem`/log when a step with a pending approval (or brake) is deleted.
- **Bundle retention.** There is *no* app-level bundle deletion — retention is external MinIO
  lifecycle. A manual gate invites multi-day waits, after which the S3 objects may be reaped while
  the `ArtifactBundle` entity survives. Approve's `bundles.TryGet` guard catches the entity case;
  a vanished-objects approve fails late in the K8s job. Accepted; documented, not solved here.
- **`re-promote` on a Manual step is stale.** `GetLastPromotedBundle` (`JobService.cs:104`) returns
  the last *job's* bundle, but parking creates no job → it returns the previous approval, not the
  pending one. **Refuse `re-promote` when a pending slot exists**; tell the operator to approve.

## Decisions

- **Latest-wins, not a per-release queue.** If B, B2, B3 pile up, only B3 is approvable; you
  cannot approve an older known-good while skipping a suspect newer. This matches the stated intent
  ("approve the latest, not the one running") and suits a homelab CD tool. The skip is **surfaced
  in the UI** ("N newer builds skipped"), not silent. A per-release approval queue is out of scope.
- **Brake is a hard override on top of the gate.** Manual decides *whether to park*; brake decides
  *whether approval is permitted*. A braked manual step still parks (latest-wins) and refuses
  approval until unblocked.

## Sequencing

1. Land exactly-once `OnGroupCompleted` + two-thread test (prerequisite spec). **Hard gate.**
2. Add atomic compare-and-set / compare-and-clear to `AttachmentStore`.
3. Build the gate: `GateMode` + reconcile, pending slot + persistence, approve/reject,
   re-promote guard, read model + UI, adjacent-manual-steps integration test.

## Scope

- In: gate mode as config, pending slot, approve/reject, latest-wins parking, persistence,
  read-model/UI, the concurrency primitive.
- Out: per-release approval queue; bundle retention/GC; matrix/fan-out; brake semantics (unchanged).
