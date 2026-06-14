# In-repo GitOps pipeline configuration — design spec

**Date:** 2026-06-14
**Status:** Design approved → ready for `writing-plans`
**Repo:** OliverVea/Olve.Pipelines

## Goal

Every pipeline points at a repo. The service continuously reconciles the live
pipeline to match a declarative config file in that repo (`<repo>/.pipelines/`).
The repo config is the **sole source of truth for pipeline *configuration*, which
is git-only**: the API exposes **no configuration-mutation endpoints** for a bound
pipeline. There is nothing to "revert" because the config write never lands —
config CRUD is rejected, not undone. **Operational** API actions stay available
(manual promotion/override, re-triggering production, cancelling jobs, setting
secret *values*); only the *shape* of the pipeline is owned by git. In the user's
words: "every pipeline just points at a repo with the pipeline config — more sense
and safer."

This builds on the declarative model that **already exists** in
`src/Olve.Pipelines/Pipelines/Sync/`: `PipelineDocument`, `Build()` (export),
and `Create()` (from-document, create-only). The missing piece this feature adds
is **apply/upsert + repo-source + reconcile loop**.

## Scope

In v1:

- Declarative YAML config in `<repo>/.pipelines/`, compiled to `PipelineDocument`.
- Per-pipeline binding (repo + path), persisted in the S3 snapshot.
- Poll-based reconcile loop that converges live state to desired state.
- Name-keyed diff (add/update/delete) applied under a coarse lock.
- Built-in deploy trigger relocated onto the binding (out of the reconciled set).
- Secrets declared by name; values stay in k8s, never in repo.
- Missing-secret status surfacing: API field + frontend badge.
- Cutover: the single live pipeline is migrated by hand (extract settings to a
  markdown reference, recreate from scratch under the new model). No backwards
  compatibility, no migration tooling.

Out of v1 (note in spec, keep schema extensible):

- **Automated on-ramp (`BuildYaml()` export of a live pipeline to a `.pipelines/`
  tree).** Replaced by the manual cutover for v1; revisit if multiple pipelines
  ever need onboarding.
- **Backwards compatibility / snapshot migration.** None — the old prod snapshot
  is discarded at cutover; the persistence format may change freely.
- Run-config snapshotting (freezing each run's image+script at start). This would
  make runs immune to reconcile and later REMOVE the in-flight drain — explicitly a
  FUTURE improvement, not v1.
- reconcile/deploy/concurrency/notification policy sections. Only identity fields
  land now.

---

## Section 1 — Architecture & ownership

1. **Ownership: full GitOps, config is git-only.** The repo config file is the
   sole source of truth for pipeline *configuration*. **Reconcile is the only
   writer of config entities.** The API exposes **no config-mutation endpoints**
   for a bound pipeline — config CRUD is *rejected*, not reverted (there is nothing
   to revert because the write never lands). **Operational** API actions remain
   fully available: manual promotion/override, re-triggering production, cancelling
   jobs, and setting secret *values*. The dividing line is **config (git-owned) vs.
   operations (API-allowed)**. Endpoint lockdown lands in Phase 4, alongside
   reconcile taking ownership as the config writer (locking config edits earlier
   would leave a bound pipeline neither editable nor reconciled).

2. **Sync mechanism: poll-based**, reusing the existing `PollTriggerService`
   pattern (`BackgroundService`, ~60s per-binding interval). Fetch → compare
   cursor → reconcile on change.

3. **Registration: per-pipeline binding — MANDATORY.** Every pipeline is bound to
   exactly one GitHub repo. Binding fields: **repo, branch, path** (to
   `.pipelines/`), **credentialsSecret**, the **deploy cursor** (`lastDeployedSha`,
   decision 4), and `ReconcileStatus`. There is no unbound pipeline state. Prefer
   creating the binding at pipeline creation (pipeline + binding together); if
   create and bind stay separate API steps, an unbound pipeline is a *draft* that
   is not reconciled and has no deploy trigger yet. The binding persists in the S3
   snapshot alongside the other stores.

   - **Composition direction: binding depends DOWN on pipeline, never up.** The
     binding holds a `PipelineId`; `Pipeline`/`PipelineService` know nothing about
     bindings. "Create pipeline + bind it" is composed at the **endpoint** (the web
     layer, from a create-with-repo request), not by the base `PipelineService`
     reaching into the binding service. Cascade-delete is event-driven (the binding
     layer subscribes to `pipelineEvents.OnDeleted`). Do NOT auto-create the binding
     from a `pipelines.OnAdded` subscription — snapshot load fires `OnAdded`, which
     would spawn spurious bindings on every restart.

   - **`credentialsSecret` is a reference, never a raw value.** It names a key in
     the pipeline's k8s secret (`olve-pipeline-{id:N}`) holding the GitHub read
     token; resolved at fetch time via the existing `$SECRET` machinery. Raw
     tokens must never land in the S3 snapshot (plaintext-at-rest).
   - **`branch`** scopes the config cursor — the `.pipelines/` subtree SHA is read
     on this branch (decision 12).
   - **Multiple source types** are supported via the `IConfigSource` seam; v1
     ships the **GitHub** concrete implementation only.

4. **Built-in deploy trigger = a poll on the bound repo's branch head, derived
   from the binding** — NOT an inbound webhook. The system is pull-based (see the
   live setup: the recommended trigger polls
   `api.github.com/repos/.../commits/main` every 60s and fires production when the
   SHA advances). So the deploy trigger needs **no name and no inbound secret** —
   nothing calls in; we call out. It is fully defined by the binding's `repo` +
   `branch` (decision 3) plus a **deploy cursor** (`lastDeployedSha`). Because the
   binding always exists, the deploy trigger always exists — **configured by
   default** when a pipeline is bound, with no manual poll-trigger authoring (which
   is what `setup-pipeline` does today).

   This is driven by a **single poll on the `branch` head** (one loop, one network
   cadence). When the head advances, the binding runs a **sequenced flow,
   config-before-build** — *reconcile the config, then propagate the commit*:

   1. **Reconcile** — if the `.pipelines/` subtree changed since the last successful
      apply, fetch + compile + apply the config diff (under the drain gate,
      Section 3). If it didn't change, this step is a no-op. Reconcile is
      level-triggered and idempotent, so "reconcile every head advance, but skip the
      fetch+compile when the subtree ETag is unchanged" is both correct and cheap.
   2. **Propagate** — only *after* reconcile succeeds (or finds nothing to do),
      enqueue a production build for the new commit.

   **Config-apply gates the build.** A commit that changes both code and config
   reconciles the new config first, then builds on it — the ordering is structural,
   not racy, and there is no second cursor racing the first. The two SHAs the
   binding tracks (`lastSyncedSha` for config, `lastDeployedSha` for the build) are
   just *state advanced in sequence by the one poll*, not two competing pollers. The
   `.pipelines/` subtree ETag (decision 5) is the optimization that skips the config
   fetch+compile on code-only pushes; it is **not** a separate poll.

   The deploy trigger lives on the binding, structurally OUTSIDE the reconciled
   `Trigger` collection. The config file's `triggers:` are purely **additive**
   (aux repos, scheduled/time deploys, upstream-update polls; an inbound webhook
   deploy, if ever wanted, is just an additive production trigger here); reconcile
   owns that additive list with delete-to-match. Relocating the deploy trigger onto
   the binding MUST land before delete-to-match on triggers is enabled — else the
   first reconcile deletes the live deploy trigger.

5. **Config-change detection = ETag on the `.pipelines/` subtree** (an optimization
   *inside* the single branch-head poll of decision 4, not a second poll). Within
   the poll, decide whether config work is needed by issuing a conditional
   `GET /repos/{o}/{r}/commits?path=.pipelines&sha={branch}&per_page=1` with
   **`If-None-Match`**:
   - **304 Not Modified** (free, no rate-limit cost) ⇒ `.pipelines/` unchanged ⇒
     skip fetch+compile, go straight to the build.
   - **200** ⇒ config changed ⇒ fetch + compile + reconcile, *then* build.

   (The `contents` API on a directory returns an array with no single sha; the
   `git/trees` subtree sha is the content-addressed alternative but its ETag burns
   on any push.) This endpoint's ETag changes only when `.pipelines/` changes, so
   idle polls and code-only pushes cost nothing for the config check. Auth = 5000
   req/hr per token (GitHub App installs scale higher) — ample at homelab scale. The
   detector isn't content-addressed (a revert-to-identical re-fetches), but
   reconcile is idempotent so that's a harmless no-op. **`lastSyncedSha` advances
   only on a fully successful apply** (Section 4); **`lastDeployedSha` advances after
   the build is enqueued** (decision 4).

---

## Section 2 — Config format & file layout

**Format: YAML**, compiled to the existing `PipelineDocument`. New dep:
`YamlDotNet`. The existing `System.Text.Json` polymorphic deserializer is reused
only for the **leaf trigger-target** types, not the top level.

```
<repo-root>/.pipelines/
  config.yaml   # manifest: apiVersion, name, description, version,
                #           secrets[], triggers[](additive),
                #           processingSteps[], productionSteps[]
  steps/        # OPTIONAL per-step files; a step may $ref steps/<name>.yaml
                #   (inline by default, extract when big)
  scripts/      # bash scripts, one per file; steps use
                #   scriptFile: scripts/x.sh (NOT inline script)
```

- **Processing-step order = list index** (0,1,2,3). No explicit `order:` field —
  removes the manual-int footgun.
- A step uses `script:` inline OR `scriptFile:` — both set = validation error.
- `description`/`version` live in a thin **`PipelineManifest` wrapper**
  `(description, version, PipelineDocument)`, NOT by polluting `PipelineDocument`.

**Secrets:** declared by NAME ONLY in a `secrets:` block (+ optional description).
VALUES never in repo — they stay out-of-band, and they are **strictly segmented
per pipeline**: every pipeline's secrets live in its own dedicated k8s secret
`olve-pipeline-{pipelineId:N}` and nowhere else. No shared/global secret store, no
cross-pipeline access — a pipeline can only ever resolve `$SECRET:NAME` against its
own `olve-pipeline-{pipelineId:N}`. Set via the secrets API / kubectl. Referenced
via `$SECRET:NAME` (headers / env). The deploy/config-source credential
(`credentialsSecret`, Section 1.3) is just another key in that same per-pipeline
secret — no special-casing.

- Validation: a `$SECRET:X` used but NOT declared in `secrets:` = config error
  (reject the reconcile).
- Declared-but-unset = allowed/applied.
- Delete-to-match applies to the **declaration list only**. Reconcile NEVER prunes
  k8s secret values.

**Missing-secret surfacing (v1):** the pipeline status/document endpoint reports
each declared secret's set/unset state (checked against the k8s secret). The
existing `frontend/` shows a warning badge listing unset declared secrets.

---

## Section 3 — Reconcile flow

**Atomicity = stage-and-swap via name-keyed diff (Approach B).** NOT
wholesale-replace — no swap primitive exists in `EntityStore`.

1. Fetch + parse + validate the desired `PipelineManifest` (outside the lock).
2. Compute the add/update/delete **diff** between desired and live, **matching by
   NAME**, outside the lock.
3. Apply `Set`/`Delete` per changed entity **inside the lock**.
   - Unchanged steps keep their IDs → preserves job-history resolution.
   - Rename = delete + recreate (name is the identity key).
4. Advance the cursor only after the full diff applies.

**Determinism rule (enforced by the active drain, b):** a config change takes
effect on the NEXT run, never the one mid-flight — the in-flight bundle drains to
completion on its original config before the mutate.

### Concurrency model — three pieces, all required for v1

**(a) Make `EntityStoreIndex` thread-safe via an immutable-backed store + a locked
`Mutate` primitive.** MANDATORY anti-crash; lands first. Today `EntityStoreIndex`
is a plain `Dictionary<TKey, HashSet<Id>>` with no locking, and `GetForKey`
returns the live `HashSet`. `GetByPipelineId` iterates it, so a concurrent
reconcile `Delete` throws `InvalidOperationException`. The fix backs each key with
`ImmutableHashSet<Id>` and funnels all writes through one locked read-modify-write
`Mutate(key, set => newSet)` method; `GetForKey` returns the immutable reference
(no copy) so reads are lock-free and snapshot-safe — the *caller* decides whether
to snapshot, preserving enumeration performance. This also yields an atomic
whole-key swap (`Mutate(key, _ => desiredSet)`) the reconciler reuses. The index
keys on `PipelineId` (which never changes on update), so not subscribing to
`OnUpdated` stays correct — document this assumption.

**(b) Active drain: pause → drain → mutate → resume.** Reconcile actively
quiesces the pipeline rather than passively deferring. This is a CORRECTNESS
requirement: a "run" is not one JobGroup —
`TriggerExecutionService.ExecuteProduction` creates a production JobGroup, and
processing steps run as SEPARATE downstream JobGroups (chained by
`Jobs/DownstreamTriggerService.cs`), each read lazily when its trigger fires.
`KubernetesJobExecutor` reads `…StepService.TryGetConfiguration(...)` at
job-execution time, so a mid-run reconcile would leak new config into
not-yet-run steps.

Sequence when a reconcile is needed:

1. **Pause** — set a per-pipeline "reconcile pending" flag. The trigger layer
   refuses to start NEW production runs for the pipeline while paused. In-flight
   chains are NOT paused — they promote step-to-step to completion (so an in-flight
   bundle finishes entirely on the config it started with → preserves the
   determinism rule, decision 11).
2. **Drain** — wait until the pipeline is quiescent: **no job in `Scheduled` or
   `InProgress`** (terminal = `Done`/`Obsolete`/`Cancelled`/`Failed`). Drain is
   *quiescence, not success* — a failed step stops the chain (`OnGroupFailed`, no
   downstream promotion) and its failed job is terminal, so a half-failed run
   drains just like a successful one. No lock held during this wait (B5).
3. **Mutate** — acquire the global config lock (c), apply the diff, advance the
   cursor, release.
4. **Resume** — clear the pause flag; queued/poll triggers fire again (poll-based
   triggers self-heal by re-detecting their source on the next interval).

- **Drain predicate:** `JobService._byPipeline` filtered by status. Artifact-bundle
  status is production-only and NOT a whole-chain signal — job status is.
- **Generous drain timeout (anti-wedge).** Active pause has the opposite risk to
  passive defer: a genuinely stuck `InProgress` job (hung k8s job) would block new
  runs forever. So drain waits with a **deliberately generous** timeout (default
  ~2h, configurable; far longer than any legitimate chain). On timeout: abort this
  reconcile attempt, **resume** (clear the flag), retry next poll — never wedge the
  pipeline.
- **Closes B4 by design:** at mutate time there are zero non-terminal jobs, so
  reconcile can never delete a step out from under a `Scheduled` job. Terminal
  jobs hold step IDs only for history (job-history display must tolerate a missing
  step).
- **No starvation:** pausing new runs guarantees the drain completes (modulo the
  timeout), unlike the passive defer this replaces.

**(c) One coarse global config-mutation lock.** Held only around the **synchronous
mutate** (step 3: diff-apply + cursor advance) and honored by the executor's
config-read / run-initiation and the trigger layer's pause-check. **Never** wraps
the drain wait (b, step 2) or any K8s/S3 I/O (B5). Coarse-and-correct over
fine-grained.

---

## Section 4 — Error handling & testing

### Error handling

Reconcile is **level-triggered**, not edge-triggered: each poll compares
desired-vs-live and converges. The subtree-SHA cursor is only an optimization to
skip work. Governing rule:

> **The cursor advances only on a fully successful apply.** Any
> fetch/parse/validation/defer outcome leaves the cursor unchanged, so the next
> poll retries automatically.

The live pipeline is **never partially mutated and never destroyed on an error.**

| Class | Examples | Effect on live pipeline | Surfaced as |
|---|---|---|---|
| **Fetch** | repo unreachable, 401/403, `.pipelines/` 404, SHA fetch fails | **None** — keep last-good, retry next poll | status `error`, problems[] |
| **Parse/validate** | bad YAML; `script`+`scriptFile` both set; `$SECRET:X` undeclared; trigger→step dangling; duplicate step names | **None** — whole reconcile rejected atomically | status `error`, Olve.Results problems[] |
| **Apply** | a `Set`/`Delete` mid-diff | Effectively infallible (in-memory `ConcurrentDictionary`). Only a *crash* mid-apply matters | — (see crash recovery) |
| **Deferred** | bundle in-flight (gate b) | **None** — marked pending, retried next poll | status `pending (run in flight)` |

**Crash recovery (decision):** rely on the next reconcile. A crash mid-apply may
leave the reloaded S3 snapshot partially applied; the next poll re-converges since
reconcile is idempotent. No snapshot-before-apply guard in v1 — consistent with
the level-triggered design.

**A 404 / missing config file does NOT trigger delete-to-match** — that would wipe
the live pipeline the moment a repo is misconfigured. Missing source ⇒ error
status, no mutation.

**Per-pipeline `ReconcileStatus`** (new, on the binding, exposed via the
document/status endpoint + frontend badge): `lastSyncedSha`, `lastSyncTime`,
`result` (Success/Error/Pending), `problems[]`, and the declared-secret set/unset
map.

**Error surfacing (decision):** status field + frontend badge only in v1. No
push/notification machinery.

### Testing

**Seam:** introduce `IConfigSource` (GitHub impl + in-memory fake) so reconcile is
testable without network. `IdProvider`/`TimeProvider` seams already exist.

**Unit (sync core, synchronous):**

- YAML → `PipelineManifest`/`PipelineDocument` compilation (golden files).
- Diff: add / update / delete / rename=delete+recreate, by name; unchanged steps
  keep IDs.
- Validation rules (each row of the parse/validate table).
- Declared-secret set/unset computation.
- `EntityStoreIndex`: concurrent `GetForKey` iteration + `Delete` doesn't throw
  and returns a copy (the Section 3a regression test).

**Integration (`RunIntegrationTests=true`):**

- Bind → reconcile → live state matches desired.
- Idempotent: second reconcile is all no-ops, cursor stable.
- **Config-mutation API endpoints are rejected for a bound pipeline** (config is
  git-only); **operational** endpoints (manual promotion/override, re-trigger,
  cancel, set secret value) still succeed.
- **Config-before-build ordering:** a head advance that changes both code and
  `.pipelines/` reconciles the new config first, then the enqueued production build
  runs on the reconciled config (single sequenced poll).
- Gate: reconcile deferred while bundle in-flight, applied after terminal step.
- Deploy build survives reconcile (it's the binding's branch-head poll, outside the
  reconciled trigger set).

---

## Cutover (manual, one-time)

No automated on-ramp in v1 and no backwards compatibility. The single live
pipeline is migrated by hand: (1) extract its current settings to a markdown
reference (image/script/env per step, triggers, declared secret names +
descriptions — via `GET /api/pipelines/{id}/document` and the `setup-pipeline`
skill); (2) author `<repo>/.pipelines/config.yaml` + `scripts/` from it;
(3) discard the old S3 snapshot, recreate the pipeline under the new model (create
+ bind), let the first reconcile build it, set secrets out-of-band, verify deploy.
Automated `BuildYaml()` export is a FUTURE improvement (see out-of-scope).

---

## Source map (verified 2026-06-14)

All paths under `src/Olve.Pipelines/`:

- `Pipelines/Sync/PipelineDocument.cs` — declarative model:
  `PipelineDocument(ApiVersion, Name, ProductionSteps[], ProcessingSteps[],
  Triggers[])`; `[JsonPolymorphic]` trigger targets
  (production/processing/poll); `StepConfigurationDocument(Image, Script,
  EnvironmentVariables)`.
- `Pipelines/Sync/PipelineDocumentEndpoints.cs` — `GET
  /api/pipelines/{id}/document` (export); `POST /api/pipelines/from-document`
  (create-only). The reconciler is the missing in-place sibling.
- `Pipelines/Sync/PipelineDocumentCreator.cs` — `ValidateInternalReferences`
  (trigger→processing-step by name), `ResolveTarget`/`ToStepConfiguration`
  helpers worth reusing. Matching by NAME (`processingByName`).
- `Pipelines/Polling/PollTriggerService.cs` — reuse template for the config-poll
  loop; `$SECRET:NAME` resolution via `KubernetesClient.GetSecretAsync`.
- `Shared/EntityStore.cs` — `ConcurrentDictionary`, per-entity `Set`/`Delete`
  firing `OnAdded`/`OnUpdated`/`OnDeleted`. No whole-collection swap, no reader
  lock.
- `Shared/EntityStoreIndex.cs` — plain `Dictionary<TKey, HashSet<Id>>`, no
  locking, `GetForKey` returns the live set; subscribes to OnAdded/OnDeleted not
  OnUpdated. → the mandatory thread-safety fix (3a).
- `Pipelines/Triggers/TriggerExecutionService.cs` — a run is NOT one JobGroup;
  processing steps run as separate downstream JobGroups read lazily. → gate is
  bundle-lifetime.
- `Jobs/JobService.cs` — `Job` stores only step-id references, not materialized
  image+script.
- `Jobs/KubernetesJobExecutor.cs` — reads config lazily at job-execution time →
  the gate (3b) is a correctness requirement.
- `Shared/Persistence/ConfigurationPersistenceService.cs` — snapshots stores → S3
  `configuration.json` (debounced), reloads at startup. Crash-before-flush
  reloads last-good.

---

## Implementation phasing (for `writing-plans`)

1. **`EntityStoreIndex` thread-safety (3a)** — anti-crash, lands first,
   independently shippable + testable. **DONE.**
2. **Binding skeleton + lifecycle** — entity `(Id, PipelineId, CreatedAt)`, CRUD
   service, S3 persistence, event-driven cascade-delete. No deploy fields (deploy is
   a poll, decision 4 — modeled with the repo fields in Phase 3), no auto-creation
   (composition lands in Phase 3's create-with-repo endpoint). **DONE.**
3. **`IConfigSource` + binding source fields + YAML compile (Sections 1–2)** —
   extends the binding with repo/branch/path/credentials + deploy cursor; the
   create-with-repo endpoint composes pipeline + binding so the deploy poll is
   configured **by default** (decision 4); relocates the deploy trigger off the
   reconciled set onto this binding-derived poll (must precede trigger
   delete-to-match in Phase 4).
4. **Reconcile diff + concurrency gate + lock (Section 3)** + **config-endpoint
   lockdown** (git-only config, decision 1) + **prepend reconcile to the Phase 3
   branch-head poll** (config-before-build, decision 4).
5. **`ReconcileStatus` + secret status surfacing + frontend badge**.
6. **Cutover** — manual extract-to-markdown + recreate from scratch (no code; no
   automated on-ramp, no migration).
