using System.Text.Json.Nodes;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>Cancels CI runs and their attempts (the watchers then delete the attempts' Jobs) and the build a run waited for. Transient.</summary>
public class CiCancellationService(
    CiRunService runs, CiAttemptService attempts, CiSchedulingLock schedulingLock, CiDefinitionService definitions, ILogger<CiCancellationService> logger)
{
    /// <summary>Ends a running run as <paramref name="status"/>, cancelling its scheduled and in-progress attempts.</summary>
    public Result<CiRun> End(CiRun run, RunStatus status, CiCancelReason? reason = null, int? supersededBy = null) => schedulingLock.Run(() =>
    {
        if (!runs.TryGet(run.Id, out var current))
            return new NotFoundProblem("CI run #{0} not found.", run.Number);
        if (current.Status.IsTerminal())
            return new ConflictProblem("CI run #{0} is already {1}.", run.Number, current.Status.ToString().ToLowerInvariant());

        // The run ends first: its attempts' cancellation then finds it ended instead of ending it as plain cancelled.
        var ended = runs.Complete(run.Id, status, reason, supersededBy);
        foreach (var attempt in attempts.ListByRun(run.Id).Where(a => a.Status.IsActive()))
            attempts.Cancel(attempt.Id).DiscardResult();
        if (current.Building)
            definitions.Release(current);
        logger.LogInformation("CI run #{RunNumber} of pull request #{Pull} in pipeline '{PipelineId}' ended {Status}{Reason}",
            run.Number, run.Pull, run.PipelineId, status, reason is { } r ? $" ({r})" : supersededBy is { } n ? $" by #{n}" : "");
        return ended;
    });

    /// <summary>Cancels the pull request's running runs (it closed, merged or left the binding's branch).</summary>
    public void CancelPull(Id<Pipeline> pipelineId, int pull, CiCancelReason reason)
    {
        foreach (var run in runs.ListByPull(pipelineId, pull).Where(r => r.Status is RunStatus.Running))
            End(run, RunStatus.Cancelled, reason).DiscardResult();
    }
}

/// <summary>Why no CI run started for a pull request.</summary>
public enum CiStartSkip
{
    /// <summary>The pipeline's active version declares no CI.</summary>
    NoCi,

    /// <summary>The pull request doesn't merge onto the base branch.</summary>
    Conflict,

    /// <summary>A newer push replaced the head being started; that push starts its own run.</summary>
    Moved,
}

/// <summary>What starting a CI run did: a run, or why none started.</summary>
public sealed record CiStart(CiRun? Run, CiStartSkip? Skipped)
{
    public static CiStart Of(CiRun run) => new(run, null);
    public static CiStart Skip(CiStartSkip why) => new(null, why);
}

/// <summary>
/// Starts CI runs of pull requests. It reads the pull request from GitHub until GitHub has decided whether it merges
/// (<c>mergeable</c> is null while GitHub computes it), then tests GitHub's merge commit with the CI definition of the
/// pipeline's active version. A pull request that changes the definition itself runs with its own: the run waits for
/// the build of its tested commit (<see cref="CiDefinitionService"/>). A new run supersedes the pull request's running
/// run. Transient.
/// </summary>
public class CiRunStartService(
    PullRequestService pulls,
    IPullRequestSource source,
    PipelineConfigBindingService bindings,
    PipelineActivationService activations,
    PipelineVersionService versions,
    RunPlanCache plans,
    CiPlanCache ciPlans,
    ConfigSnapshotService configSnapshots,
    DeployPollService configSync,
    CiRunService runs,
    CiCancellationService cancellation,
    CiScheduler scheduler,
    CiSchedulingLock schedulingLock,
    CiDefinitionService definitions,
    TimeProvider time,
    ILogger<CiRunStartService> logger)
{
    /// <summary>The waits between reads of a pull request whose mergeability GitHub hasn't decided yet.</summary>
    internal static readonly TimeSpan[] MergeableBackoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];

    /// <param name="expectedHead">For an automatic start: the head that was debounced; a pull request that moved on since starts nothing.</param>
    /// <param name="outdated">The run replaces a result the active version's new checks made outdated.</param>
    public async Task<Result<CiStart>> StartAsync(Id<Pipeline> pipelineId, int number, string? expectedHead = null, CancellationToken ct = default,
        bool outdated = false)
    {
        if (bindings.GetByPipelineId(pipelineId).TryPickProblems(out var bindingProblems, out var binding))
            return bindingProblems;
        if (!pulls.TryGet(pipelineId, number, out var tracked))
            return new NotFoundProblem("Pull request #{0} not found in pipeline '{1}'.", number, pipelineId);
        if (tracked.State != PullRequestState.Open)
            return new ConflictProblem("Pull request #{0} is {1}.", number, tracked.State.ToString().ToLowerInvariant());
        if (!activations.TryGet(pipelineId, out var activation) || activation is not PipelineActivation.Active active
            || !versions.TryGet(active.VersionId, out var version))
            return new ConflictProblem("Pipeline '{0}' has no active version to run CI with.", pipelineId);
        var activeHasCi = !ciPlans.Get(version.Id).TryPickProblems(out _);

        if ((await DecidedAsync(binding, number, ct)).TryPickProblems(out var readProblems, out var reported))
            return readProblems;
        pulls.Observe(pipelineId, binding.Branch, reported, time.GetUtcNow());
        if (expectedHead is not null && reported.Head.Sha != expectedHead)
            return CiStart.Skip(CiStartSkip.Moved);
        if (reported.Mergeable == false)
        {
            logger.LogInformation("Pull request #{Pull} of pipeline '{PipelineId}' doesn't merge onto '{Branch}': no CI run", number, pipelineId, binding.Branch);
            return CiStart.Skip(CiStartSkip.Conflict);
        }

        if (reported.MergeCommitSha is not { Length: > 0 } tested)
            return new UnavailableProblem("GitHub reported no merge commit for pull request #{0}.", number);
        if ((await source.GetCommitAsync(binding, tested, ct)).TryPickProblems(out var commitProblems, out var commit))
            return commitProblems;
        if (commit.Parents.Length == 0)
            return new UnavailableProblem("Merge commit {0} of pull request #{1} has no parents.", tested, number);

        if ((await ChangesDefinitionAsync(binding, version, tested, ct)).TryPickProblems(out var compareProblems, out var ownDefinition))
            return compareProblems;
        if (!ownDefinition && !activeHasCi)
            return CiStart.Skip(CiStartSkip.NoCi);

        var problems = new List<string>();
        var config = ownDefinition ? (null, null) : PinVersionConfig(pipelineId, version, problems);
        var input = CiInputs.Pull(binding.Repo, tested, number);

        var run = schedulingLock.Run(() =>
        {
            var created = runs.Create(new CiRunRequest(pipelineId, number, reported.Title, reported.Head.Ref, reported.Draft, reported.Head.Sha,
                tested, commit.Tree.Sha, commit.Parents[0].Sha, version, input, config.Snapshot, config.Config, problems, ownDefinition,
                Outdated: outdated));
            foreach (var older in runs.ListByPull(pipelineId, number).Where(r => r.Id != created.Id && r.Status is RunStatus.Running))
                cancellation.End(older, RunStatus.Superseded, supersededBy: created.Number).DiscardResult();
            scheduler.Advance(created.Id);
            return created;
        });
        definitions.BuildFor(run);
        logger.LogInformation("Started CI run #{RunNumber} of pull request #{Pull} ({HeadSha} merged as {TestedSha}) in pipeline '{PipelineId}' {Definition}",
            run.Number, number, reported.Head.Sha, tested, pipelineId, ownDefinition ? "with its own definition" : $"on version {version.Number}");
        return CiStart.Of(run);
    }

    /// <summary>The pull request once GitHub has decided whether it merges, read again with backoff while it hasn't.</summary>
    private async Task<Result<GitHubPullRequest>> DecidedAsync(PipelineConfigBinding binding, int number, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            if ((await source.GetAsync(binding, number, ct)).TryPickProblems(out var problems, out var reported))
                return problems;
            if (reported is null)
                return new NotFoundProblem("GitHub has no pull request #{0} in '{1}'.", number, binding.Repo);
            if (reported.Mergeable is not null || i >= MergeableBackoff.Length)
                return reported.Mergeable is null
                    ? new UnavailableProblem("GitHub hasn't decided whether pull request #{0} merges.", number)
                    : reported;
            await Task.Delay(MergeableBackoff[i], time, ct);
        }
    }

    private const int GitHubCompareFileLimit = 300;

    /// <summary>
    /// The tested commit changes a watched path against the commit of the active version's binary, or GitHub's list of
    /// changed files is cut short so it may: then it needs a definition of its own.
    /// </summary>
    internal async Task<Result<bool>> ChangesDefinitionAsync(PipelineConfigBinding binding, PipelineVersion version, string tested, CancellationToken ct)
    {
        if (version.CommitSha is not { } built)
            return false;
        if ((await source.ChangedFilesAsync(binding, built, tested, ct)).TryPickProblems(out var problems, out var files))
            return problems;
        var watched = configSync.WatchedPaths(binding);
        return files.Count >= GitHubCompareFileLimit
               || files.Any(f => watched.Any(w => w.Length == 0 || f == w || f.StartsWith(w + "/", StringComparison.Ordinal)));
    }

    internal (int? Snapshot, string? Config) PinVersionConfig(Id<Pipeline> pipelineId, PipelineVersion version, List<string> problems) =>
        plans.Get(version.Id).TryPickProblems(out _, out var plan)
            ? (null, null)
            : CiConfig.Pin(pipelineId, plan.Manifest, $"version {version.Number}", configSnapshots, problems);
}
