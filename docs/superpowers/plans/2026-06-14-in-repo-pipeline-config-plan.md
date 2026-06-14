# Implementation plan — In-repo GitOps pipeline configuration

**Spec:** [2026-06-14-in-repo-pipeline-config-design.md](../specs/2026-06-14-in-repo-pipeline-config-design.md)
**Date:** 2026-06-14
**Status:** Plan written → awaiting review before code

Each phase is independently shippable and testable. Stop after each phase for
review. No phase enables trigger delete-to-match before Phase 2 lands.

---

## Phase 1 — `EntityStoreIndex` thread-safety (mandatory anti-crash)

**Why first:** Today `EntityStoreIndex.GetForKey` (`Shared/EntityStoreIndex.cs:52`)
returns the live `HashSet`, and `*Service.GetByPipelineId` iterates it. A
concurrent reconcile `Delete` mutates that set mid-iteration →
`InvalidOperationException`. This must be safe before any reconcile code exists.

**Approach: immutable-backed index + a locked `Mutate` primitive.** Back the
index with `ImmutableHashSet<Id<T>>` per key instead of a mutable `HashSet`, and
funnel every write through one locked read-modify-write method. This gives
lock-free, zero-copy, snapshot-safe reads (the caller controls whether to
snapshot) and — bonus — an atomic whole-key swap primitive the reconciler can
reuse. Chosen over clone-on-read so enumeration performance and snapshot
responsibility stay with the caller.

**Changes — `Shared/EntityStoreIndex.cs`:**

- Field becomes `Dictionary<TKey, ImmutableHashSet<Id<T>>> _index` + a single
  private lock.
- Add the primitive:

  ```csharp
  private void Mutate(TKey key, Func<ImmutableHashSet<Id<T>>, ImmutableHashSet<Id<T>>> mutate)
  {
      lock (_gate)
      {
          var current = _index.TryGetValue(key, out var ids) ? ids : ImmutableHashSet<Id<T>>.Empty;
          var next = mutate(current);
          if (ReferenceEquals(next, current)) return;   // no-op
          if (next.IsEmpty) _index.Remove(key);
          else _index[key] = next;
      }
  }
  ```

- `Add`/`Remove` handlers collapse to `Mutate(key, s => s.Add(id))` /
  `Mutate(key, s => s.Remove(id))`.
- `GetForKey` takes the lock only to read the reference out of the `Dictionary`
  (the `Dictionary` itself isn't safe for concurrent read+write), then returns
  the **immutable reference** — no copy. The caller enumerates it lock-free and
  snapshots only if it needs stability across an `await`. `ContainsKey` likewise
  guarded.
- Document the assumption: the index keys on `PipelineId`, which never changes on
  update, so not subscribing to `OnUpdated` stays correct.
- Note for Phase 4: reconcile can swap a key's whole set atomically via
  `Mutate(key, _ => desiredSet)` — the swap primitive the spec said was missing.

**Tests** (`tests/Olve.Pipelines.Tests`, unit):

- Concurrent `GetForKey` enumeration + `Delete` (via store events) on the same
  key does not throw — the returned immutable set is a stable snapshot.
  (Regression test for the crash.)
- A reference obtained from `GetForKey` is unaffected by a subsequent
  `Add`/`Remove` (proves immutability/non-aliasing).
- `Mutate` no-op short-circuit: a mutator returning the same reference leaves the
  stored set untouched; emptying a key removes it from `_index`.

**Done when:** `dotnet test` green; new concurrency test reproduces the old crash
against the pre-fix code and passes after.

---

## Phase 2 — Deploy-trigger → binding refactor

**Why before reconcile:** decision 4. The built-in deploy trigger must move OUT of
the reconciled `Trigger` collection and onto the binding, or the first reconcile's
delete-to-match wipes it.

**Changes:**

- Introduce the binding entity (minimal here; full source-fields in Phase 3):
  `PipelineConfigBinding(Id<Pipeline> PipelineId, <deploy-trigger fields>)`.
- Relocate the deploy trigger's identity off the reconciled `Trigger` store onto
  the binding. The deploy path reads it from the binding.
- Reconcile (Phase 4) will own the `Trigger` store with delete-to-match; the
  deploy trigger is structurally excluded because it no longer lives there.

**Tests:**

- Deploy still fires from the binding-sourced trigger (existing deploy behavior
  unchanged).
- The reconciled trigger set excludes the deploy trigger.

**Note:** This is a model refactor, but **no in-place data migration** — the live
pipeline is recreated from scratch at cutover (see Cutover runbook), so there is no
need to migrate an existing deploy trigger onto a binding on a running instance.
The refactor only has to be correct for *newly created* pipelines.

---

## Phase 3 — `IConfigSource` + binding model + YAML compile

**Changes:**

- **`IConfigSource`** seam (`Pipelines/Sync/ConfigSource/`):
  - `GitHubConfigSource` — cursor via
    `GET /repos/{o}/{r}/commits?path=.pipelines&sha={branch}&per_page=1` with
    per-binding **ETag / `If-None-Match`** (304 = free, no rate-limit cost);
    fetches the `.pipelines/` tree to compile only on cursor change; auth token
    from the binding's `credentialsSecret` via `KubernetesClient.GetSecretAsync`
    (mirror `PollTriggerService`'s `$SECRET` pattern).
  - `FakeConfigSource` (test project) — in-memory tree + SHA.
- **Binding model extended:** `PipelineConfigBinding(PipelineId, Repo, Branch,
  Path, CredentialsSecret, <deploy-trigger>, ReconcileStatus)`. `CredentialsSecret`
  is a **reference** (key name in the pipeline k8s secret), never a raw token —
  raw values must not reach the S3 snapshot. Persisted via its own `EntityStore`
  so it rides the existing S3 snapshot (`ConfigurationPersistenceService`).
- **YAML compile** (`Pipelines/Sync/`):
  - Add `YamlDotNet` to `Directory.Packages.props`; reference from the project.
  - `PipelineManifest(Description, Version, PipelineDocument)` wrapper type.
  - `ManifestCompiler`: YAML → `PipelineManifest`. Resolves `$ref steps/<name>.yaml`
    and `scriptFile: scripts/x.sh`. Reuses the existing `System.Text.Json`
    polymorphic deserializer ONLY for leaf trigger-target types.
  - Validation (reject whole reconcile on any): `script`+`scriptFile` both set;
    `$SECRET:X` undeclared in `secrets:`; trigger→step dangling (reuse
    `PipelineDocumentCreator.ValidateInternalReferences`); duplicate step names.

**Tests:**

- YAML → `PipelineManifest` golden-file compilation (inline + `$ref` + `scriptFile`).
- Each validation rule rejects with the right `Olve.Results` problem.
- `FakeConfigSource` round-trips tree + SHA.

---

## Phase 4 — Reconcile diff + concurrency gate + lock

**Changes:**

- **`PipelineReconciler`** (sync core): given desired `PipelineManifest` + live
  state, compute name-keyed add/update/delete diff for production steps,
  processing steps (order = list index), triggers (additive set, delete-to-match),
  and the secret declaration list. Unchanged steps keep IDs; rename =
  delete+recreate. Apply `Set`/`Delete` per entity.
- **Active drain — pause → drain → mutate → resume** (decision 3b, resolved
  B3/B4):
  1. **Pause:** set a per-pipeline "reconcile pending" flag; the trigger layer
     refuses NEW production runs while set. In-flight chains keep promoting to
     completion (old config — preserves decision 11).
  2. **Drain:** wait until **no job in `Scheduled`/`InProgress`**
     (`JobService._byPipeline` + status filter). Quiescence, not success. **No
     lock held during the wait** (B5).
  3. **Mutate:** acquire the global config lock (3c), apply the diff, advance the
     cursor, release. Lock wraps the sync section only.
  4. **Resume:** clear the flag.
  - **Generous drain timeout (anti-wedge):** default ~2h, configurable; on timeout
    abort + resume + retry next poll (a hung `InProgress` job must never wedge new
    runs).
- **Trigger-layer pause hook:** `TriggerExecutionService` (and the deploy/poll
  trigger paths) check the pause flag before creating a production run; refused
  fires rely on poll re-detection to self-heal.
- **`ReconcileLoopService`** (`BackgroundService`, ~60s, per binding, mirrors
  `PollTriggerService`): fetch subtree SHA → if changed → fetch+compile+validate →
  pause → drain (timeout) → diff+apply under lock → resume → **advance cursor only
  on success**.

**Tests** (integration, `RunIntegrationTests=true`):

- Bind → reconcile → live matches desired.
- Idempotent: second reconcile = no-ops, cursor stable.
- Manual API mutation reverted on next reconcile.
- **Pause blocks new runs:** a trigger fired while reconcile is pending does not
  start a production run; it proceeds after resume.
- **Drain waits for in-flight chain** then mutates; in-flight bundle completed on
  OLD config (decision 11).
- **Half-failed run still drains:** a failed step stops the chain and reconcile
  proceeds (quiescence, not success).
- **Drain timeout:** a stuck `InProgress` job → reconcile aborts + resumes + retries
  (pipeline not wedged).
- Gate: deferred while bundle in-flight, applied after terminal step.
- Deploy trigger survives reconcile.
- Error classes (unit + integration): fetch 404/auth, parse/validate failure,
  deferred — none mutate live state; cursor not advanced.

---

## Phase 5 — `ReconcileStatus` + secret status surfacing + frontend badge

**Changes:**

- `ReconcileStatus(lastSyncedSha, lastSyncTime, result{Success|Error|Pending},
  problems[], secretStates: name→set|unset)` on the binding.
- Secret states computed against k8s secret `olve-pipeline-{pipelineId:N}`.
- Expose on the document/status endpoint (extend
  `PipelineDocumentEndpoints.cs`).
- `frontend/`: warning badge listing unset declared secrets + reconcile
  error/pending state. Regenerate Kiota client after the API change.

**Tests:**

- Secret set/unset computation (unit).
- Status endpoint shape (integration).

---

## Phase 6 — Cutover (manual, one-time — no automated on-ramp)

**Decision (user 2026-06-14): no backwards compatibility, recreate from scratch.**
The automated `BuildYaml()` exporter is **dropped from v1** (moved to future, see
out-of-scope). The single live pipeline is migrated by hand, not by tooling.

**Runbook (operational, not code):**

1. **Extract** the current prod pipeline settings to a markdown reference doc:
   per production/processing step — image, script, env; the triggers; the declared
   secret names + descriptions. (Source: `GET /api/pipelines/{id}/document` +
   `setup-pipeline` skill as the current source of truth.)
2. **Author** `<repo>/.pipelines/config.yaml` + `scripts/` from that reference.
3. **Cutover:** discard the old S3 snapshot (no migration), recreate the pipeline
   under the new model (create + bind to repo), let the first reconcile build it
   from `config.yaml`, set secrets via the secrets API/kubectl, verify deploy.

**No code, no round-trip test** — the recreate is a one-time manual step. If
multiple pipelines ever need onboarding, revisit automated `BuildYaml()` (future).

---

## Cross-cutting conventions (from CLAUDE.md)

- .NET 10, file-scoped namespaces, nullable, implicit usings.
- Sync core; async only at I/O boundaries (GitHub, k8s, S3).
- `Olve.Results` everywhere, no exceptions. No raw `Guid` — `Id<T>` only.
- Package versions in `Directory.Packages.props` (no `Version` in csproj).
- Named endpoints (`.WithName(...)`). AOT-compatible deps only.
- Transient by default; singleton only for state-holding stores.
- Rocks for mocking; TUnit assertions; `Olve.Results.TUnit` for Result asserts.
