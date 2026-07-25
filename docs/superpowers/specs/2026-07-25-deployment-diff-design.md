# Deployment diff in run history — design

**Date:** 2026-07-25
**Status:** Revised after adversarial review → **blocked on prerequisites, see §Blockers**
**Repo:** OliverVea/Olve.Pipelines

## Summary

Each row in a step's **Run history** should answer "what changed in this deployment?" — both
**what code** it built and **what configuration** ran it.

We record the built commit SHA on the **`ProductionJobGroup`**, pin the build to that SHA so the
record is causal rather than observational, snapshot the pipeline configuration that ran it, and
render code comparisons as **deep links** into the forge.

The one rule that survived review and carries the design: **nothing calls the forge on a read
path.** Everything is captured at write time, when the binding is current by definition and
credentials are already in hand.

> **Revision note.** The first draft of this doc rested on a principle ("store facts about the run,
> never facts about the repository") that does not survive scrutiny — §4 stores repo file content
> deliberately, and §2 stores commit subject/author. The real criterion is **volume and
> self-sufficiency**, not provenance. It also asserted several things about the codebase that are
> false; those are corrected inline and listed in §Corrections.

## Today

Nothing links a run to a commit.

- `ArtifactBundle` is `(Id, PipelineId, CreatedAt, Status)` — no provenance.
- `TriggerExecutionService.ExecuteProductionForPipeline` (`Pipelines/Triggers/TriggerExecutionService.cs:62`)
  takes only an `Id<Pipeline>`; `bundles.Create` at `:79` has nothing to stamp.
- `DeployPollService.DeployAsync` (`Pipelines/Sync/DeployPollService.cs:189`) resolves the branch
  head and writes it to the cursor at `:227`, but nothing carries it into the run.
- The only git identity persisted is `PipelineConfigBinding.LastDeployedSha` / `LastSyncedSha`:
  two mutable cursors with no history.
- `step-detail-view.ts:460-465` renders a live **Configuration** panel beside historical runs,
  implying a relationship it cannot back up.

### The build is not pinned to a commit

`KubernetesJobSpec` carries no repo/ref/SHA, and `KubernetesClient.cs:294` injects nothing implicit.
Source acquisition happens inside the step script, which fetches `tarball/$BRANCH`. The poll
observes head `X`; the job — scheduled, queued, possibly retried — later fetches whatever `main`
points at. A recorded trigger SHA would not describe what was built.

`olve_version()` is `date +%Y%m%d-%H%M%S` (`olve-lib.sh:24-26`), and that timestamp becomes
`--set image.tag`, so deployed image tags have no traceable relationship to a commit either.

## Blockers

These must be resolved before the design is buildable. Two are pre-existing bugs.

### B1 — `ArtifactBundle` is not persisted at all

`UploadArtifactBundleAsync` / `DownloadArtifactBundleAsync` (`Shared/Persistence/S3BundleStore.cs:33,51`)
have **zero callers** repo-wide; `BundlePersistenceService` (`:20-22`) is load-only and reads a
prefix nothing writes. `ArtifactBundle` appears in no snapshot — not `ConfigurationSnapshot`, not
`JobSnapshot`, not `PromotionSnapshot`. Bundle *content* goes to S3 via the runner's `mc mirror` to
a different key space entirely (`Kubernetes/KubernetesClient.cs:348`). The codebase already
acknowledges this: re-promote guards against a vanished bundle
(`Pipelines/Processing/ProcessingStepEndpoints.cs:116-117`).

`.pipelines/scripts/deploy.sh:31` helm-upgrades the `olve-pipelines` prod release, so **the app
restarts inside its own pipeline run** — every self-deploy wipes the bundle store.

**Resolution: put `CommitSha` on `ProductionJobGroup`, not `ArtifactBundle`.** `JobGroup` is in
`jobs.json` via `JobSnapshot`, survives restart, and is already where §3 puts the config snapshot.
The bundle id remains the join key, so the semantics are unchanged — source is still anchored to
the production run that produced the bundle — only the storage location moves to one that persists.

(If bundle persistence is ever wired up, note `S3BundleStore.cs:72-77` reconstructs every restored
bundle with `ArtifactBundleStatus.Completed` hardcoded, resurrecting failed builds as successful.)

### B2 — Two `bundles.Create` sites, and one skips the pause guard

`TriggerExecutionService.cs:79` **and** `PipelineEndpoints.cs:63`. The latter is a copy-paste of the
trigger flow behind `POST /api/pipelines/{id}/trigger/production` that omits the
`pauseState.IsPaused` check at `TriggerExecutionService.cs:69` — so the API can start production
while a reconcile is pending, which the service path refuses. **Pre-existing bug; fix independently.**
Until the paths converge, any SHA threading must cover both or the manual endpoint silently mints
SHA-less runs.

### B3 — `PipelineDocument` has no canonical form

§3 hashes "canonical PipelineDocument JSON", but canonicalization is unspecified and the ordering is
not stable by construction:

- `ProductionStepService.GetByPipelineId` (`:26-36`) and `TriggerService.GetByPipelineId` (`:31-41`)
  apply **no ordering**; only processing steps are sorted (`ProcessingStepService.cs:35`).
  `PipelineDocumentBuilder` passes the unordered ones straight through.
- A reconcile that deletes and recreates a step (`PipelineReconciler.cs:89-98`) or a restart
  repopulating the store flips serialized order with identical config.
- `null` vs empty `env` are treated as equal by `ConfigEquals` (`PipelineReconciler.cs:247`) so
  reconcile never normalizes them, but they serialize differently.
- `Dictionary<string,string>` env key order is insertion order, never sorted.

**Must specify:** sort production steps and triggers by name, sort env keys, normalize null↔empty.
Otherwise the "config changed" signal fires spuriously on runs where nothing changed.

### B4 — `PipelineDocumentBuilder` cannot build documents for GitHub-webhook triggers

`BuildTargetDocument` (`Pipelines/Sync/PipelineDocumentBuilder.cs:76-82`) handles production,
processing and poll targets and falls through to `ResultProblem` for `GitHubWebhookTarget` — which
`PipelineReconciler.cs:242` creates from a `type: github` trigger in config. Round-trip asymmetry:
reconcile in, cannot serialize out. **Pre-existing bug**, currently latent (no config in this repo
declares one). Blocks §3 for any pipeline that does.

## Changes

### 1. Binding: forge coordinates

`PipelineConfigBinding` has no provider and no host (`GitHubConfigSource.cs:20` hardcodes
`ApiBase`). Both fields default, so existing `configuration.json` snapshots deserialize unchanged.
Serialize the enum as an int and do not reorder members, matching `BindingDeployTrigger`.

```csharp
SourceProvider Provider = SourceProvider.GitHub,   // GitHub | GitLab | Gitea
string? ApiBaseUrl = null,                         // null => provider default
```

Needed only to construct URLs. **No per-forge API client is required.**

### 2. Production job group: source provenance

```csharp
public record ProductionJobGroup(
    Id<JobGroup> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt,
    Id<ArtifactBundle> ArtifactBundleId,
    string? CommitSha = null, string? CommitSubject = null, string? CommitAuthor = null)
    : JobGroup(Id, PipelineId, CreatedAt);
```

Subject/author make the list readable instead of a column of hex, and matter more than they look:
`pl job list` / `pl job get` (`src/Olve.Pipelines.Cli/Commands/Jobs/JobCommands.cs:8,76`) are
browserless, so for the CLI the stored text **is** the entire feature — a deep link renders as
nothing there.

**Where the SHA comes from.** Only the deploy-poll path has it today: `GetBranchHeadShaAsync` has
one production call site (`DeployPollService.cs:193`), which covers poll, binding-webhook and
reconcile-now (via `ReconcileNowAsync → PollBindingAsync → DeployAsync`). Thread it from there.

For the remaining paths (manual `trigger/production`, `PollTriggerTarget`, `GitHubWebhookTarget`),
**do not resolve HEAD at trigger time.** The first draft proposed this; it would take a live,
reconcile-mutated binding to stamp a fact about a run — the same fault used to reject `CompareAsync`
— and would make the synchronous `TriggerExecutionService` async, against CLAUDE.md's sync-core
rule. These paths record `null` and render "source unknown".

For the legacy `GitHubWebhookTarget` path specifically, the push payload's `after` field is the
causally correct SHA and is currently discarded — `GitHubWebhookPayload.cs:6-7` parses only `ref`.
Parsing `after` is cheaper and more accurate than a HEAD lookup.

### 3. Job group: execution snapshot

Source provenance and execution config have different lifetimes. A **re-promote** runs *today's*
deploy script against *that* old code — correct, and verified: deploy scripts consume the bundle
(`olve_bundle_input`, `version.txt`, staged helm chart), never re-fetch source.

```csharp
// on JobGroup (base — applies to both kinds)
BindingSnapshot? Binding,      // Provider, ApiBaseUrl, Repo, Branch, Path
string? ConfigSnapshotHash,    // sha256 of canonical PipelineDocument JSON — see B3
```

Snapshot **coordinates, not cursors** — `LastDeployedSha`/`LastSyncedSha` are live state and
meaningless frozen. Omit `CredentialsSecret`: nothing at read time needs it.

Reuse `PipelineDocument` (`Pipelines/Sync/PipelineDocument.cs`), subject to B3 and B4.

**Content-address it.** `jobs.json` is a single whole-state blob rewritten on a 1s dirty timer with
no pruning — the cost of inlining is **write amplification**, not bytes. Store the document once at
`configs/{hash}.json` via `ISnapshotStore`. Note `StorageMode.Ephemeral` leaves the store null, so
the hash must degrade to "not captured" rather than dangling.

The hash doubles as a **config-changed signal** between adjacent runs, computed with no API call.

### 4. Pin the build to the SHA

Add `CommitSha` to `KubernetesJobSpec`, inject as `GIT_SHA` into `runnerEnv`, and fetch by SHA:

```sh
olve_fetch_repo "$REPO" "$GIT_SHA" "$CTX"
```

**This narrows the drift window; it does not close it.** Three commits are in play and only one
gets pinned:

| What | Which commit |
|---|---|
| Source tarball | `GIT_SHA` (branch head at deploy) — **pinned by this change** |
| Script *text* | `LastSyncedSha` — last commit touching `.pipelines/` (`GitHubConfigSource.cs:47`), inlined by `ManifestCompiler.cs:126-137` |
| `olve-lib.sh` | whatever `main` points at when the pod starts (`build.sh:12-13`, unpinned) |

So the config hash of §3 is keyed to a different commit than the bundle SHA, and the two diffs the
UI renders side by side are measured against different points in history. Pinning `olve-lib.sh` to
`$GIT_SHA` is a follow-up worth doing; the script-text split is structural and should be documented
in the UI rather than hidden.

**Guard the empty case.** With no SHA, `olve_fetch_repo "$REPO" "" "$CTX"` becomes
`GET /repos/$REPO/tarball/` → 404 → `wget` non-zero → `set -e` kills the step. Scripts must fall
back to `$BRANCH` when `GIT_SHA` is empty. "Source unknown" is a UI state; it must not be a runner
failure.

**§4 ships with §2, not after it.** Recording a SHA without pinning renders an observational value
as fact — the exact failure the rejected changeset design was faulted for.

### 5. Rendering

Per-run: `abc123f — fix auth timeout`, plus a compare link to the previous run **at that step**.

**"Previous run at that step" needs a definition**, and the first draft's claim that the range is
"exact" is false in several cases:

- **Define it as:** the previous run at that step that reached a terminal *executed* state on a
  **distinct** bundle.
- **Exclude obsoleted/cancelled runs.** `_renderRuns` (`step-detail-view.ts:510-538`) currently
  renders every job with no status filter; latest-wins marks superseded jobs `Obsolete` and they
  never executed. Using the previous *row* would pick a bundle that never shipped.
- **Re-promote gives base == head** (`JobService.GetLastPromotedBundle:104-119`). Render "no source
  change — same bundle" rather than an empty compare.
- **Failed production bundles** must not become a base — `JobGroupCompletionService.cs:55-59` marks
  the bundle `Failed`, but nothing stops it being the newest.
- **Production rows need a join.** `ProductionJob` (`Jobs/Job.cs:18-27`) has no `ArtifactBundleId` —
  only `ProcessingJob` does. Resolving a production run's SHA needs `JobGroupId → ProductionJobGroup`,
  which requires exposing group data the API does not currently serve.
- **Force-push can invert a range.** Storing endpoints does not make this impossible; it makes it
  *silent*. The UI should treat an empty/reversed compare as a state, not an error.

The blocked-gate case the design handles correctly: a bundle held at the prod gate while beta
deploys three times legitimately spans three bundles, and `DownstreamTriggerService.cs:50-56,87-93`
halts without skipping ahead, so the per-step (not global) framing is right.

```
GitHub  https://github.com/{repo}/compare/{base}...{head}
GitLab  https://gitlab.com/{repo}/-/compare/{base}...{head}
Gitea   https://{host}/{repo}/compare/{base}...{head}
```

**Free quick win, independent of everything above:** `PipelineBindingStatus`
(`PipelineBindingEndpoints.cs:209-210`) already sends both SHAs, but `_renderBindingBadge()`
(`pipeline-detail-view.ts:207-232`) renders only `repo@branch` and pills. Pure frontend.

## Optional: stored commit list (phase 6)

Deep links are unreachable from two surfaces that matter: the `pl` CLI (no browser) and **failure
handlers**, which get only `PIPELINE_*` env vars (`FailureHandlers/FailureContext.cs:22-34`) and run
with no forge credentials. An aoe triage agent asking "what changed in this deploy?" cannot click a
link.

§2's subject/author already covers the CLI. If the triage case proves it worthwhile, capture a
commit list at write time — on the **binding-webhook path it is free**, since the push payload
already carries `commits[]` and we discard it:

```csharp
ChangesetCapture(string Base, string Head, Commit[] Commits, bool Truncated)
```

Storing **endpoints alongside commits** makes chain validity a local `bᵢ.Head == bᵢ₊₁.Base` check,
so best-effort capture degrades to `Commits = []` with gaps rendered explicitly rather than silently
under-reported. Intern as a blob like §3; do not inline into `jobs.json`. Files and patches stay
behind the deep link.

## Rejected

**Calling the forge on a read path.** The load-bearing rule. A read-time `CompareAsync` taking a
live, reconcile-mutated `PipelineConfigBinding` would silently diff the wrong repo after a rebind,
and would need live credentials to render history. Note the fault is the *read path*, not the
signature — `CompareAsync(BindingSnapshot, base, head)` has neither defect, and the first draft's
signature critique was a strawman of a fix the doc had already invented.

**Composing commit ranges without storing endpoints.** Gaps become undetectable. Storing
`(Base, Head)` per capture makes completeness checkable, which is why phase 6 above is viable.

**Not rejected, contrary to the first draft: storing changesets per se.** The "second source of
truth" argument does not hold — the doc stores repo file content in §3 deliberately, `LastDeployedSha`
is already stored forge state that the app *branches on* (`DeployPollService.cs:201,207`), and pod
logs are already mirrored to S3 for the same reason (ephemeral upstream). The drift argument
inverts: a compare link whose base was force-pushed away 404s silently in the user's browser, and
the app cannot notice either, whereas a stored record retains the as-of-deploy answer. Storage was
never the objection (~0.2–0.6 MB/pipeline/year at this repo's ~550 commits/yr).

## Sequencing

1. **B2** — converge the two production-trigger paths, restoring the pause guard. Independent bug.
2. **B3 + B4** — canonical ordering, and the missing `GitHubWebhookTarget` case. Independent bug.
3. `Provider` + `ApiBaseUrl` on the binding. **Must precede step 4** — snapshots written before it
   freeze incomplete records.
4. `BindingSnapshot` + `ConfigSnapshotHash` on `JobGroup`; document interned to `configs/{hash}.json`.
5. `CommitSha` + subject/author on `ProductionJobGroup`, threaded from `DeployAsync` — **together
   with** `GIT_SHA` injection and the empty-SHA script guard. Not separable.
6. Frontend: SHA + subject per run, compare link to the previous executed run at that step.
7. Optional: stored commit list for failure-handler triage.

## Non-goals & known gaps

- **No backfill.** Existing runs have no SHA. The first run after binding has no base either (the
  cursor seeds without building, `DeployPollService.cs:207-213`), and every rebind re-seeds.
- **Gaps are labelled, not eliminated.** Unbound pipelines, non-deploy-poll triggers, and pre-feature
  runs all render "source unknown".
- **Compare pages need forge access** — unreachable for a viewer without repo permissions.
- **Rollback hazard.** `JobPersistenceService.cs:71-76` treats unparseable `jobs.json` as terminal
  (`LogCritical` + throw, "manual restore required"). Rolling back to a pre-change binary makes the
  old code read new-format `jobs.json`. Unknown members are ignored by default so it should survive,
  but this is untested and the failure mode is a crashlooping pod in the app that deploys its own fix.
- **AOT.** `BindingSnapshot` and any nested types must be reachable from `JobPersistenceJsonContext`.
- Multi-forge beyond URL construction remains a separate project.

## Corrections to the first draft

| Claimed | Actual |
|---|---|
| "the single `bundles.Create` call" | Two: `TriggerExecutionService.cs:79`, `PipelineEndpoints.cs:63` |
| "bundles are persisted in two places" | **Zero** write paths; `ArtifactBundle` does not survive restart (B1) |
| `PipelineDocument` "already round-trips" | False for `type: github` triggers (B4) |
| "the range is exact" | False for production rows, re-promote, obsoleted rows, force-push (§5) |
| "closes the drift race" | Narrows it; script text and `olve-lib.sh` remain unpinned (§4) |
| subject/author are "free" | Free on the deploy-poll path only; other paths have no such call |
| "makes gaps impossible" | Gaps are relabelled "source unknown", not eliminated |
| union of changesets == branch log | False — cursor seeds without building, rebinds re-seed, four bundle-creation paths never touch the cursor |
