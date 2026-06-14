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

## Phase 2 — Binding skeleton + lifecycle  *(DONE)*

**Scope correction (2026-06-14, user review):** the original "deploy-trigger →
binding refactor" doesn't hold under the **pull model**. The live deploy mechanism
is a *poll* on `api.github.com/repos/.../commits/main` (see `setup-pipeline`
Option A), not an inbound webhook. So the deploy trigger has **no name and no
secret** — it's a branch-head poll fully defined by the binding's `repo`/`branch`
(Phase 3) plus a deploy cursor. It therefore can't be modeled without the repo
fields, and its relocation moves to **Phase 3**. Phase 2 is just the binding
machinery.

**Changes (implemented):**

- `PipelineConfigBinding(Id, PipelineId, CreatedAt)` — identity only; source fields
  + deploy cursor land in Phase 3.
- `PipelineConfigBindingService` — CRUD (`Create`/`TryGet`/`GetByPipelineId`/`Delete`),
  indexed by `PipelineId`.
- S3 persistence: `PipelineConfigBindingData` in the snapshot; save/load + dirty
  subscriptions in `ConfigurationPersistenceService`.
- **Event-driven cascade-delete** (`PipelineConfigBindingCleanupService` +
  `PipelineConfigBindingEventRegistration`, subscribing to
  `pipelineEvents.OnDeleted`) — the binding layer depends DOWN on pipeline.
- **No** `PipelineService → PipelineConfigBindingService` dependency, **no**
  auto-creation on `pipelines.OnAdded` (would spawn spurious bindings on snapshot
  reload). Creation composition is deferred to Phase 3's create-with-repo endpoint.

**Tests (unit):** binding CRUD + `GetByPipelineId`; unbound pipeline = draft
(lookup fails); pipeline delete cascades binding deletion; deleting one pipeline
leaves another's binding intact.

---

## Phase 3 — `IConfigSource` + binding model + YAML compile  *(DONE)*

**Deviations from the original plan (2026-06-14, flagged for review):**
- **YAML compile uses a YAML→`JsonNode` DOM bridge + `System.Text.Json` source-gen**, NOT
  YamlDotNet's `StaticDeserializerBuilder` + the `Vecc` analyzer. YamlDotNet's reflection-free
  `YamlStream` parser produces the DOM; `$ref`/`scriptFile` are spliced at the node level; the
  assembled tree deserializes through a source-gen `ManifestJsonContext`, reusing the existing
  `[JsonPolymorphic]` trigger-target metadata. Fully AOT-safe, no third-party analyzer, simpler.
- **`ReconcileStatus` deferred to Phase 5** (added Repo/Branch/Path/CredentialsSecret/
  LastDeployedSha only) — nothing in Phase 3 writes or reads it; pre-cutover so no migration cost.
- **Deploy poll seeds its cursor without building on first observation** (mirrors
  `PollTriggerService`) — restarts and freshly-bound pipelines don't trigger a surprise rebuild.
- **`from-document` binding** realized as a separate `with-repo` + bind-existing endpoint pair
  rather than folding repo fields into `from-document` (a document+bind combo is redundant once
  Phase 4 reconcile populates a bound pipeline from `config.yaml`).

**Changes:**

- **`IConfigSource`** seam (`Pipelines/Sync/ConfigSource/`):
  - `GitHubConfigSource` — cursor via
    `GET /repos/{o}/{r}/commits?path=.pipelines&sha={branch}&per_page=1` with
    per-binding **ETag / `If-None-Match`** (304 = free, no rate-limit cost);
    fetches the `.pipelines/` tree to compile only on cursor change; auth token
    from the binding's `credentialsSecret` via `KubernetesClient.GetSecretAsync`
    (mirror `PollTriggerService`'s `$SECRET` pattern).
  - `FakeConfigSource` (test project) — in-memory tree + SHA.
- **Binding model extended:** `PipelineConfigBinding(Id, PipelineId, Repo, Branch,
  Path, CredentialsSecret, LastDeployedSha, ReconcileStatus, CreatedAt)`. No
  deploy-trigger name/secret — the deploy trigger is the **branch-head poll** the
  binding derives, with `LastDeployedSha` as its cursor (decision 4).
  `CredentialsSecret` is a **reference** (key name in the pipeline k8s secret),
  never a raw token — raw values must not reach the S3 snapshot. Already persisted
  via its own `EntityStore` (Phase 2).
- **Create-with-repo endpoint** composes pipeline + binding at the web layer (NOT
  in `PipelineService`), so binding to a repo configures the deploy poll **by
  default** — no manual poll-trigger authoring. The `from-document` create path
  binds too.
- **Branch-head poll** (mirror `PollTriggerService`): the **single** per-binding
  poll of `branch` head; fire production when `LastDeployedSha` advances. In Phase 3
  this poll only *builds* (no reconcile exists yet) — it replaces `setup-pipeline`
  Option A as the deploy mechanism and relocates the live deploy trigger off the
  reconciled `Trigger` store (must precede Phase 4 delete-to-match). **Phase 4
  prepends the config-reconcile step to this same loop** (config-before-build,
  decision 4) — it is NOT a second poll.
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

## Phase 4 — Reconcile diff + concurrency gate + lock  *(DONE)*

**Integration tests (2026-06-14):** `GitOpsBindingTests` (container-backed, `RunIntegrationTests=true`)
cover the HTTP surface that needs the wired app: create-with-repo composes pipeline+binding,
**git-only lockdown** rejects config mutation on a bound pipeline but allows an unbound one, the
binding status endpoint is readable (degrades gracefully when k8s is unconfigured), and pipeline
delete cascades the binding — 4/4 green. The deeper loop behaviours (drain waits, pause-blocks-runs,
deploy-survives) stay at the sync unit/coordinator level: the containerized app can't swap
`IConfigSource` for a fake, and those paths are already covered by the reconciler/coordinator unit
tests.

**Deviations (2026-06-14, flagged for review):**
- **No `Swap` index primitive added.** The reconciler uses the spec's Approach B (per-entity
  `Set`/`Delete` by name diff), which works at the `EntityStore` level — the index updates via
  events. A whole-key index swap is never invoked, so it was dropped (the Phase-1 carry-forward
  note assumed it would be used). Atomicity comes from the drain (zero non-terminal jobs at mutate)
  plus the global lock.
- **`LastSyncedSha` added to the binding** (config cursor, symmetric with `LastDeployedSha`); the
  config **ETag** is kept in-memory in `DeployPollService` (pure rate-limit optimization). The rest
  of `ReconcileStatus` (result/problems/secret map) remains Phase 5.
- **Config-before-build gating:** a failed config fetch/compile/reconcile **holds off the build**
  for that cycle (returns before deploy), so a broken config never ships code on stale config.

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
- **Config-endpoint lockdown (req 2, git-only config):** the existing
  config-mutation endpoints (step create/update/delete, config attach, trigger
  CRUD, `from-document` apply, etc.) are **rejected for a bound pipeline** — reconcile
  is now the sole config writer. Operational endpoints (manual promotion/override,
  re-trigger production, cancel job, set secret *value*) stay open. A pipeline-bound
  guard returns a `Result` problem (e.g. `409`/`Conflict`) on config writes. Lands
  here (not Phase 3) so reconcile exists as the replacement writer the moment edits
  are locked.
- **Extend the Phase 3 branch-head poll into config-before-build** (decision 4 — NOT
  a new `BackgroundService`): on head advance → conditional subtree-ETag check → if
  `.pipelines/` changed: fetch+compile+validate → pause → drain (timeout) →
  diff+apply under lock → resume → **advance `lastSyncedSha` only on success** →
  then the existing build-enqueue (advance `lastDeployedSha`). 304 / no-config-change
  skips straight to the build.

**Tests** (integration, `RunIntegrationTests=true`):

- Bind → reconcile → live matches desired.
- Idempotent: second reconcile = no-ops, cursor stable.
- **Config-mutation endpoints rejected for a bound pipeline; operational endpoints
  still succeed** (git-only config, req 2).
- **Config-before-build ordering:** head advance touching both code and
  `.pipelines/` reconciles first, then the build runs on the reconciled config.
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

## Phase 5 — `ReconcileStatus` + secret status surfacing + frontend badge  *(DONE)*

**Status (2026-06-14):**
- **Backend:** `ReconcileStatus` (result/lastSyncTime/problems/declaredSecrets) recorded on the
  binding by `DeployPollService` after every reconcile attempt (success + all error paths);
  `GET /api/pipelines/{id}/binding/status` returns it plus **live** secret set/unset computed
  against the k8s secret (graceful — `IsSet: null` when k8s is unreadable, not a false "unset").
- **Frontend:** regenerated the Kiota TS client (kiota CLI installed); `pipeline-detail-view`
  shows a GitOps badge — repo@branch, reconcile synced/error+first-problem/pending, and an
  unset-secrets warning. Unbound pipelines tolerate the missing binding. Builds clean.

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
