# Deployment diff in run history — design

**Date:** 2026-07-25
**Status:** Design → ready for review
**Repo:** OliverVea/Olve.Pipelines

## Summary

Each row in a step's **Run history** should answer "what changed in this deployment?" — both
**what code** it built and **what configuration** ran it.

The controlling principle is that Olve.Pipelines stores **facts about the run**, never **facts
about the repository**. "This bundle was built from `abc123`" is ours to record — the forge cannot
know it. "These twelve commits sit between `abc123` and `def456`" belongs to the forge, which
renders it better and is always more correct. So we persist a commit SHA per bundle and render
comparisons as **deep links** into the forge. No changesets, no patches, no compare API on any
read path.

## Today

Nothing links a run to a commit.

- `ArtifactBundle` is `(Id, PipelineId, CreatedAt, Status)` — no provenance.
- `TriggerExecutionService.ExecuteProductionForPipeline` (`Pipelines/Triggers/TriggerExecutionService.cs:62`)
  takes only an `Id<Pipeline>`; `bundles.Create` at `:79` has nothing to stamp.
- `DeployPollService.DeployAsync` (`Pipelines/Sync/DeployPollService.cs:189`) resolves the branch
  head, compares it to the cursor, fires production — and **discards the SHA**.
- The only git identity persisted is `PipelineConfigBinding.LastDeployedSha` / `LastSyncedSha`:
  two mutable cursors, overwritten every cycle, no history.
- `step-detail-view.ts:465` renders a live **Configuration** panel beside historical runs, implying
  a relationship it cannot back up.

### The build is not pinned to a commit

`KubernetesJobSpec` carries no repo/ref/SHA, and `KubernetesClient.cs:294` injects nothing implicit
— runner env is the step's own vars plus `envFrom` the pipeline secret. Source acquisition happens
inside the step script:

```sh
REPO=OliverVea/Olve.Pipelines; BRANCH=main
olve_fetch_repo "$REPO" "$BRANCH" "$CTX"   # GET /repos/$REPO/tarball/$BRANCH
```

The poll observes head `X`, then the job — scheduled, queued, possibly retried — later fetches
`tarball/main`, which may be `Y`. **A recorded trigger SHA would not describe what was built.**
This race exists today; it is only invisible because nothing claims a commit.

`olve_version()` is `date +%Y%m%d-%H%M%S`, and that timestamp becomes `version.txt` →
`--set image.tag=$VERSION`, so deployed image tags have no traceable relationship to a commit
either.

## Changes

### 1. Binding: forge coordinates

`PipelineConfigBinding` has no provider and no host (`GitHubConfigSource.cs:20` hardcodes
`ApiBase = "https://api.github.com"`). Both fields default, so existing `configuration.json`
snapshots deserialize unchanged. Serialize the enum as an int and do not reorder members, matching
`BindingDeployTrigger`.

```csharp
SourceProvider Provider = SourceProvider.GitHub,   // GitHub | GitLab | Gitea
string? ApiBaseUrl = null,                         // null => provider default
```

Needed only to construct URLs. **No per-forge API client is required for this feature.**

### 2. Bundle: source provenance

```csharp
public record ArtifactBundle(
    Id<ArtifactBundle> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt,
    ArtifactBundleStatus Status,
    string? CommitSha = null, string? CommitSubject = null, string? CommitAuthor = null);
```

Subject/author make the list readable instead of a column of hex, and are **free**:
`GetBranchHeadShaAsync` already fetches the full commit object from
`GET /repos/{repo}/commits/{branch}` and discards everything but the SHA. They are provenance of
*this build*, not an index of the repository.

Thread a `string? commitSha` through `ExecuteProductionForPipeline` to the single `bundles.Create`
call. **Also update `ArtifactBundlePersistedData(Id, PipelineId, CreatedAt)`** in `S3BundleStore`
(`bundles/artifact/{bundleId}.json`) — bundles are persisted in two places, and a bundle restored
after restart would otherwise silently lose its SHA.

Non-deploy-poll trigger paths (manual `trigger/production`, `PollTriggerTarget`, webhook) resolve
HEAD-of-branch via the existing `IConfigSource.GetBranchHeadShaAsync` at trigger time — one call,
and it keeps history uniform. Unbound pipelines record `null` and render "source unknown".

### 3. Pin the build to the SHA

Add `CommitSha` to `KubernetesJobSpec`, inject it as `GIT_SHA` into `runnerEnv`, and fetch by SHA
in the scripts. The tarball endpoint accepts any ref, so `olve-lib.sh` needs no change — its
`branch` parameter is positional:

```sh
olve_fetch_repo "$REPO" "$GIT_SHA" "$CTX"
```

This makes the recorded SHA **causal rather than observational**, and closes the drift race that
exists today. Injection is forge-neutral; adapting the fetch is per-repo script authoring
(GitLab: `/projects/{id}/repository/archive.tar.gz?sha=$GIT_SHA`).

Follow-on (optional): `VERSION={timestamp}-{sha:0:7}`, making "what is running in prod" answerable
from `kubectl` alone.

### 4. Job group: execution snapshot

Source provenance and execution config have different lifetimes, so they live on different
entities. A **re-promote** runs *today's* deploy script against *that* old code — correct, and it
would be misreported if both facts hung off the bundle.

```csharp
// on JobGroup
BindingSnapshot? Binding,      // Provider, ApiBaseUrl, Repo, Branch, Path
string? ConfigSnapshotHash,    // sha256 of canonical PipelineDocument JSON
```

Snapshot the **coordinates, not the cursors** — `LastDeployedSha`/`LastSyncedSha` are live state and
meaningless frozen. Omit `CredentialsSecret`: nothing at read time needs it, and leaving it out
keeps a secret-adjacent field out of a persisted record.

Reuse `PipelineDocument` (`Pipelines/Sync/PipelineDocument.cs`) — already the reconcile target,
already round-trips, already served by `GET /api/pipelines/{id}/document`.

**Content-address it.** `jobs.json` is a single whole-state blob rewritten on a 1s dirty timer with
no retention or pruning; inlining every step's script per run would bloat it permanently. Store the
document once at `configs/{hash}.json` via the existing blob path. Config changes only on reconcile
— far rarer than deploys — so a year of daily deploys with a dozen config changes stores a dozen
documents.

The hash doubles as a **config-changed signal**: adjacent runs with differing hashes mean the
pipeline definition changed between them. That is a second diff, computed with no API call, and for
a deploy step it is arguably the more interesting one.

## Rendering

Per-run: `abc123f — fix auth timeout`, plus a compare link to the previous run **at that step**
(not globally — a bundle blocked at the prod gate while beta deploys three times legitimately spans
three bundles). Both endpoints are single fields off two bundles, so the range is exact.

```
GitHub  https://github.com/{repo}/compare/{base}...{head}
GitLab  https://gitlab.com/{repo}/-/compare/{base}...{head}
Gitea   https://{host}/{repo}/compare/{base}...{head}
```

No new endpoint and no server-side forge call: the user's own browser authenticates.

**Free quick win, independent of all the above:** `PipelineBindingStatus` already sends
`LastDeployedSha`/`LastSyncedSha` over the wire, but `pipeline-detail-view.ts::_renderBindingBadge()`
renders only `repo@branch` and the reconcile pill. Displaying the deployed SHA is pure frontend.

## Rejected

**Storing changesets** (commits + changed files per bundle, composed across ranges for
processing-step diffs). Consecutive runs are contiguous, so the union of stored changesets is the
entire branch log since binding — a mirror of data the forge owns, able to drift from it (amended
commits, force-pushes) with no way to notice. The objection is not storage (~1–2 MB/pipeline/year)
but becoming a second source of truth.

It was also internally inconsistent: capture had to be **best-effort** (a failed compare must never
fail a deploy), yet composition needed a **complete** chain. A gap would silently under-report "what
is going to prod" — precisely the claim the feature exists to make. Taking the two endpoint SHAs
instead makes gaps impossible and removes the per-file-count approximation entirely.

**`CompareAsync` on `IConfigSource`.** Its signature took a live, reconcile-mutated
`PipelineConfigBinding` to answer a question about a past run; rebinding a pipeline would silently
diff against the wrong repository. Live credentials at read time were a symptom of the same fault.
Both dissolve once nothing calls the forge on a read path.

## Sequencing

1. `Provider` + `ApiBaseUrl` on the binding. **Must precede §4** — snapshots written before it would
   freeze incomplete records.
2. `BindingSnapshot` + `ConfigSnapshotHash` on `JobGroup`; document interned to `configs/{hash}.json`.
3. `CommitSha` (+ subject/author) on `ArtifactBundle` and `ArtifactBundlePersistedData`.
4. `GIT_SHA` into runner env; scripts fetch by SHA.
5. Frontend: SHA + subject per run, compare link to the previous run at that step.

Steps 1–3 are worth landing on their own merit: they make history honest, and "which commit is
running in prod right now" becomes answerable, which today it is not.

## Non-goals & known gaps

- **No backfill.** Existing bundles have no SHA and never will. The first run after binding has no
  base either (the cursor is seeded without building). Both render "source unknown", not an error.
- **No inline summary.** "12 commits, 8 files changed" requires a click. If wanted later it arrives
  as a *cache*, which may be lossy in a way the rejected design could not be.
- **Compare pages need forge access.** Unreachable for a viewer without repo permissions.
- Multi-forge beyond URL construction (a `GitLabConfigSource`, provider-keyed `IConfigSource`
  resolution replacing the singleton at `ServiceConfiguration.cs:73`, token-vs-HMAC webhook
  verification, hook registration) remains a separate, self-contained project that touches no code
  in this design.
