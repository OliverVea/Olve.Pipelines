# Blockers — In-repo GitOps pipeline config

Resolve **one at a time, in order, before continuing** to implementation.
Spec: [design](../specs/2026-06-14-in-repo-pipeline-config-design.md) ·
Plan: [plan](2026-06-14-in-repo-pipeline-config-plan.md)

Status legend: 🔴 open · 🟡 investigating · 🟢 resolved

---

## B1 🟢 YamlDotNet under AOT — RESOLVED
**Class:** design-altering (could move decision 5).
AOT publishing is enabled and CLAUDE.md forbids AOT-incompatible libs. YamlDotNet
is reflection-heavy. If it can't run clean under AOT, the YAML-format decision
needs a source-gen path or a different lib.

**Resolution:** AOT-viable via source generation — **decision 5 stands, no design
change.** YamlDotNet ships `Vecc.YamlDotNet.Analyzers.StaticGenerator` (v16.3.0),
a Roslyn generator producing a static context; use `StaticDeserializerBuilder` /
`StaticSerializerBuilder` instead of the reflection-based `DeserializerBuilder`.
This is the direct analogue of the project's existing `JsonSerializerContext`
source-gen (used in `AppJsonContext`, `PollJsonContext`, +5 more). Confirmed
`PublishAot=true` in `Olve.Pipelines.csproj`; confirmed leaf trigger-targets
already use STJ `[JsonPolymorphic]` source-gen (`Pipelines/Sync/PipelineDocument.cs:31`).

Implementation requirements (carry into Phase 3):
1. Add `YamlDotNet` + `Vecc.YamlDotNet.Analyzers.StaticGenerator` to
   `Directory.Packages.props`.
2. Use `StaticDeserializerBuilder` with a generated static context — NEVER the
   reflection `DeserializerBuilder` (the AOT trap).
3. Annotate manifest types (`PipelineManifest`, `PipelineDocument`,
   `StepConfigurationDocument`, per-step docs) for the static generator.
4. Polymorphic leaf bridge: hand the YAML trigger-target node to the existing STJ
   polymorphic context (YAML node → JSON → STJ). Impl detail, not a blocker.

Refs: [Andrew Lock — YamlDotNet source generator for Native AOT](https://andrewlock.net/using-the-yamldotnet-source-generator-for-native-aot/),
[Vecc.YamlDotNet.Analyzers.StaticGenerator 16.3.0](https://www.nuget.org/packages/Vecc.YamlDotNet.Analyzers.StaticGenerator).

---

## B2 🟢 Deploy trigger location for an *unbound* pipeline — RESOLVED
**Class:** design-altering (decisions 3 + 4 collide).
Decision 4 puts the deploy trigger on the binding; decision 3 *read* as making the
binding optional. A pipeline with no repo binding would still need its deploy
trigger somewhere.

**Resolution (user, 2026-06-14): binding is MANDATORY, not optional.** Every
pipeline is bound to exactly one GitHub repo; that repo's **deploy trigger always
lives on the binding**; the config file's `triggers:` are purely **additive** (aux
repos, scheduled/time deploys, upstream-update polls). There is no "unbound
pipeline" state, so the collision dissolves — the deploy trigger always exists and
always lives on the binding.

Implications (carry into spec + Phase 2):
- Spec decision 3 reworded: binding is required, not optional.
- Prefer creating the binding **at pipeline creation** (pipeline + repo binding
  together) so an unbound pipeline never exists. If the existing API keeps
  create-pipeline and bind separate, an unbound pipeline is a *draft* state: not
  reconciled, no deploy trigger yet. (Impl detail for Phase 2, not a blocker.)

---

## B3 🟢 In-flight gate liveness — RESOLVED
**Class:** design-altering.
The bundle-lifetime gate defers reconcile until a bundle's processing chain
reaches its terminal step. A stuck/failed/cancelled chain could block reconcile
forever.

**Verified (code read 2026-06-14):** the completion→next-step cascade is fully
synchronous — executor sets a job `Done` → `OnUpdated` →
`JobGroupCompletionService.HandleJobUpdated` (all group jobs terminal →
`OnGroupCompleted.Invoke`) → `DownstreamTriggerService.HandleGroupCompleted`
creates the next group + `Scheduled` job, all on one call stack. Statuses (6):
non-terminal `Scheduled`/`InProgress`; terminal `Done`/`Obsolete`/`Cancelled`/`Failed`.
`ArtifactBundleStatus` (Completed/Failed) is set only for the *production* group,
so it is NOT a whole-chain signal — job status is.

**Resolution — ACTIVE DRAIN (user-chosen 2026-06-14), replaces the passive gate:**
pause → drain → mutate → resume.
- **Pause:** per-pipeline "reconcile pending" flag; trigger layer refuses NEW
  production runs while set. In-flight chains keep promoting to completion (old
  config — preserves decision 11). This is Option #2 (pause triggers, drain whole
  chain); Option #1 (pause promotions, mutate mid-chain) was rejected — it would
  break decision 11.
- **Drain:** wait until **no job in `Scheduled`|`InProgress`** (terminal =
  `Done`/`Obsolete`/`Cancelled`/`Failed`). Quiescence, not success — a half-failed
  run drains the same (failed step stops the chain, failed job is terminal). No
  lock during the wait (B5).
- **Mutate:** global config lock (3c) around the sync diff-apply + cursor advance.
- **Resume:** clear the flag; poll-based triggers self-heal by re-detecting next
  interval.
- **Generous drain timeout (anti-wedge, user: "very generous"):** default ~2h,
  configurable; far longer than any legitimate chain. On timeout → abort + resume +
  retry next poll, so a hung `InProgress` job never wedges new runs.
- **No starvation** (active pause guarantees the window) — supersedes the passive
  defer's starvation limitation.

---

## B4 🟢 Step deletion vs. scheduled jobs — RESOLVED BY DESIGN (via B3 active drain)
**Class:** implementation risk.
Reconcile does rename = delete+recreate. If a *Scheduled* (not yet in-flight) job
references a deleted step, does the existing cascade cancel it, or orphan it →
later fail in `KubernetesJobExecutor.TryGetConfiguration`?

**Resolution:** the active-drain model (B3) makes this impossible — at mutate time
there are **zero** non-terminal jobs (everything drained, new runs paused), so
reconcile never deletes a step out from under a `Scheduled`/`InProgress` job.
Remaining: terminal jobs hold step IDs only for history → **job-history display
must tolerate a missing step** (minor; verify when touching `JobEndpoints` /
history rendering).

---

## B5 🟢 Global config-lock must not wrap I/O — RESOLVED (discipline recorded)
**Class:** implementation discipline.
The coarse config-mutation lock can only cover the *sync* config read in the
executor — never the K8s/S3 async calls or the drain wait — or it serializes job
execution / risks deadlock.
**Resolution:** recorded in spec §3(c) and plan Phase 4: the lock wraps **only the
synchronous diff-apply + cursor advance**; the drain wait (B3 step 2) and all
K8s/S3 I/O run outside it. Verify during Phase 4 code review.

---

## B6 🟢 Git source: subtree-SHA mechanics, auth, rate limits — RESOLVED
**Class:** design-altering (decision 12 mechanics).
**Forge resolved (user, 2026-06-14): GitHub** (all pipelines bound to a GitHub
repo) — not a self-hosted forge. **Auth resolved:** per-binding
`credentialsSecret` referencing a key in the pipeline k8s secret
(`olve-pipeline-{id:N}`), resolved via the existing `$SECRET` machinery — never a
raw token in the S3 snapshot. **Multiple source types** via `IConfigSource` seam;
v1 = GitHub concrete only. Binding also carries `branch` (cursor read on that
branch). Still open: confirm the subtree-SHA is one cheap request (`contents` on a
dir returns an array w/o a single sha; `git/trees` gives tree shas), and rate
limits at ~60s × N pipelines.
**Credentials-as-reference CONFIRMED (user 2026-06-14):** binding stores a
reference to a k8s-secret key, never a raw token.

**RESOLVED (2026-06-14):**
- **Cursor:** `GET /repos/{o}/{r}/commits?path=.pipelines&sha={branch}&per_page=1`
  → head commit touching `.pipelines`. (The `contents` API on a dir returns an
  array with no single sha — confirmed; `git/trees/{branch}` + entry sha is the
  content-addressed alternative but its ETag burns on any push.) Commit-based
  cursor is not content-addressed → a revert-to-identical re-triggers reconcile,
  harmless (idempotent no-op).
- **Rate-limit lever:** ETag / `If-None-Match` conditional requests. A 304 Not
  Modified does **not** count against the rate limit (when authenticated). The
  commits-by-path ETag changes **only when `.pipelines` changes**, so idle polls
  AND code-only pushes return 304 (free); only a real config change costs a
  counted request (+ a few to fetch the tree to compile — rare).
- **Math:** authenticated REST = 5000 req/hr per token (GitHub App installs
  scale higher). Worst case w/o ETag = 60/hr/pipeline → ~83 pipelines/token; with
  304s free, steady-state ≈ 0. Fine at homelab scale.
- Carry into Phase 3: `GitHubConfigSource` stores the per-binding ETag + last
  cursor; polls conditionally; compiles only on cursor change.
**Resolution:** RESOLVED 🟢

---

## B7 🟢 Phase-1 race test determinism — RESOLVED
**Class:** minor.
Reproducing the crash needs forced interleaving, or the regression test is flaky.
**Resolution:** the immutable-backed index makes the fix structural, so the
**deterministic** assertion "a reference from `GetForKey` is unaffected by a later
`Add`/`Remove`" IS the regression guard — it fails on the old live-`HashSet` code
(reflects the mutation) and passes on the new code, with no interleaving. The
concurrent stress test (parallel enumerate + `Mutate`) is supplementary
("doesn't throw over N iters"), not the gate → no flakiness.

---

## B8 🟢 Snapshot schema migration — RESOLVED (moot)
**Class:** minor.
Old `configuration.json` snapshots have no bindings.
**Resolution (user 2026-06-14): no backwards compatibility.** The old prod
snapshot is **discarded** at cutover and the single pipeline is recreated from
scratch under the new model (Phase 6 manual runbook). The persistence format may
change freely; no tolerant-reload-for-old-data requirement and no migration
script. (Robust reload is still good hygiene, but not a correctness constraint.)
