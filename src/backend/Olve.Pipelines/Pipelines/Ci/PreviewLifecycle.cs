using System.Text.Json.Nodes;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// What a preview holds back in CI scheduling, read by <see cref="CiScheduler"/>: a pull request's deploy waits for its
/// running close run, and a close run's steps wait until no deploy of the pull request still has a Job (a cancelled
/// deploy's helm may run on for a moment, and a teardown before it ends would leak the preview). Transient.
/// </summary>
public class PreviewGate(CiRunService runs, CiAttemptService attempts, CiPlanCache plans, JobWatcherRegistry watchers)
{
    /// <summary>The pull request's close run that a never-reused step of <paramref name="run"/> waits for, or null.</summary>
    public CiRun? CloseRunning(CiRun run) =>
        run.Kind == CiRunKind.Pull ? runs.ListCloseRuns(run.PipelineId, run.Pull).FirstOrDefault(r => r.Status is RunStatus.Running) : null;

    /// <summary>
    /// The deploys a close run waits for: never-reused attempts of the pull request's runs that are still active or
    /// whose Job is still being deleted. Empty when it may start.
    /// </summary>
    public IReadOnlyList<CiAttempt> LiveDeploys(CiRun close)
    {
        var live = new List<CiAttempt>();
        foreach (var run in runs.ListByPull(close.PipelineId, close.Pull))
        {
            if (plans.Get(run).TryPickProblems(out _, out var plan))
                continue;
            live.AddRange(attempts.ListByRun(run.Id).Where(a =>
                plan.TryGetUnit(a.StepName, out _, out var unit) && unit.Step.NeverReused
                && (a.Status.IsActive() || watchers.IsRunning(JobWatchKey.Of(a.Id)))));
        }

        return live;
    }

    /// <summary>Completes once the watchers of <paramref name="deploys"/> are gone.</summary>
    public Task WhenStopped(IEnumerable<CiAttempt> deploys) =>
        Task.WhenAll(deploys.Select(a => watchers.WhenStopped(JobWatchKey.Of(a.Id))));
}

/// <summary>
/// A preview's way from deploy to removal: a deploy makes it live, its pull request closing or merging starts a close
/// run, a failed close run is tried again with backoff while the pull request stays closed. Event-driven; transient.
/// </summary>
public class PreviewLifecycle(
    PreviewService previews,
    CiRunService runs,
    CiAttemptService attempts,
    CiPlanCache plans,
    PullRequestService pulls,
    PipelineVersionService versions,
    CiScheduler scheduler,
    CiSchedulingLock schedulingLock,
    PreviewStatusReporter statuses,
    TimeProvider time,
    ILogger<PreviewLifecycle> logger)
{
    internal static readonly TimeSpan[] RetryBackoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    /// <summary>A CI run ended: a deploy settles its preview, a close run removes it or fails.</summary>
    public void RunEnded(CiRun run)
    {
        if (!previews.TryGet(run.PipelineId, run.Pull, out var preview))
            return;

        if (run.IsClose && preview.CloseRun == run.Id)
        {
            if (run.Status is RunStatus.Succeeded)
            {
                previews.Update(preview, p => p with { State = PreviewState.Removed, Failures = 0, RetryAt = null });
                logger.LogInformation("Removed the preview of pull request #{Pull} in pipeline '{PipelineId}' (close run #{RunNumber})",
                    run.Pull, run.PipelineId, run.Number);
            }
            else
            {
                var failures = preview.Failures + 1;
                var retryAt = time.GetUtcNow() + RetryBackoff[Math.Min(failures, RetryBackoff.Length) - 1];
                previews.Update(preview, p => p with { State = PreviewState.Failing, Failures = failures, RetryAt = retryAt });
                logger.LogWarning("Close run #{RunNumber} of pull request #{Pull} in pipeline '{PipelineId}' ended {Status}; trying again at {RetryAt} (failure {Failures})",
                    run.Number, run.Pull, run.PipelineId, run.Status, retryAt, failures);
            }

            foreach (var waiting in runs.ListByPull(run.PipelineId, run.Pull).Where(r => r.Status is RunStatus.Running))
                scheduler.Advance(waiting.Id);
            return;
        }

        if (preview.DeployedBy == run.Id && preview.State is PreviewState.Deploying)
            previews.Update(preview, p => p with { State = PreviewState.Live, Url = p.Url ?? UrlOf(run) });
    }

    /// <summary>An attempt ended: a deploy's link becomes the preview's, and a close run waiting for it may start.</summary>
    public void AttemptEnded(CiAttempt attempt)
    {
        if (!runs.TryGet(attempt.RunId, out var run) || run.IsQueue || !previews.TryGet(run.PipelineId, run.Pull, out var preview))
            return;
        if (preview.DeployedBy == run.Id && preview.Url is null && UrlOf(run) is { } url)
            previews.Update(preview, p => p with { Url = url });
        if (!run.IsClose && preview.CloseRun is { } closeId && runs.TryGet(closeId, out var close) && close.Status is RunStatus.Running)
            scheduler.Advance(close.Id);
    }

    /// <summary>A pull request closed, merged or left the binding's branch: its preview is removed.</summary>
    public void PullClosed(Id<Pipeline> pipelineId, int pull)
    {
        if (previews.TryGet(pipelineId, pull, out var preview))
            Close(preview);
    }

    /// <summary>Failing previews whose retry is due, of pull requests still closed, get a new close run.</summary>
    public void RetryDue()
    {
        var now = time.GetUtcNow();
        foreach (var preview in previews.List().Where(p => p is { State: PreviewState.Failing, RetryAt: { } at } && at <= now))
            if (!IsOpen(preview))
                Close(preview);
    }

    /// <summary>
    /// At startup: previews of pull requests that closed while the service was away are removed, and every preview
    /// reports <c>olve/preview</c> again, in case a status was lost with the process.
    /// </summary>
    public void Resume()
    {
        foreach (var preview in previews.List())
            statuses.Report(preview);
        foreach (var preview in previews.List().Where(p => p.State is PreviewState.Deploying or PreviewState.Live or PreviewState.Removing))
            if (!IsOpen(preview))
                Close(preview);
    }

    private bool IsOpen(Preview preview) =>
        pulls.TryGet(preview.PipelineId, preview.Pull, out var pull) && pull.State == PullRequestState.Open;

    /// <summary>Starts a close run with the definition and commit of the run that deployed the preview.</summary>
    private void Close(Preview preview) => schedulingLock.Run(() =>
    {
        if (!previews.TryGet(preview.PipelineId, preview.Pull, out var current) || current.State is PreviewState.Removed)
            return;
        if (current.CloseRun is { } running && runs.TryGet(running, out var close) && close.Status is RunStatus.Running)
            return;
        if (!runs.TryGet(current.DeployedBy, out var deployed) || !versions.TryGet(deployed.VersionId, out var version))
        {
            logger.LogError("Can't remove the preview of pull request #{Pull} in pipeline '{PipelineId}': CI run #{RunNumber} that deployed it is gone",
                current.Pull, current.PipelineId, current.DeployedByNumber);
            previews.Update(current, p => p with { State = PreviewState.Failing, RetryAt = null });
            return;
        }

        var repo = JsonNode.Parse(deployed.Input)?["repo"]?.GetValue<string>() ?? "";
        var created = runs.Create(new CiRunRequest(deployed.PipelineId, deployed.Pull, deployed.Title, deployed.Branch, deployed.Draft, deployed.HeadSha,
            deployed.TestedSha, deployed.Tree, deployed.MainSha, version, CiInputs.Close(repo, deployed.TestedSha, deployed.Pull),
            deployed.ConfigSnapshot, deployed.Config, OwnDefinition: deployed.OwnDefinition, Kind: CiRunKind.Close));
        previews.Update(current, p => p with { State = PreviewState.Removing, CloseRun = created.Id, RetryAt = null });
        logger.LogInformation("Started close run #{RunNumber} for the preview of pull request #{Pull} in pipeline '{PipelineId}', deployed by CI run #{DeployedBy}",
            created.Number, current.Pull, current.PipelineId, deployed.Number);
        scheduler.Advance(created.Id);
    });

    private string? UrlOf(CiRun run)
    {
        if (plans.Get(run).TryPickProblems(out _, out var plan))
            return null;
        return attempts.ListByRun(run.Id)
            .Where(a => plan.TryGetUnit(a.StepName, out _, out var unit) && unit.Step.NeverReused)
            .SelectMany(a => a.Links ?? [])
            .FirstOrDefault()?.Url;
    }
}

/// <summary>Runs <see cref="PreviewLifecycle.RetryDue"/> every minute.</summary>
public class PreviewRetryLoop(IServiceProvider sp, TimeProvider time, ILogger<PreviewRetryLoop> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, ct);
                sp.GetRequiredService<PreviewLifecycle>().RetryDue();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Retrying preview removals failed");
            }
        }
    }
}

/// <summary>
/// The <c>olve/preview</c> status on the head the preview was deployed from: where the preview is. It never reports
/// <c>failure</c>, since a failed deploy is <c>olve/ci</c>'s to report. Transient.
/// </summary>
public class PreviewStatusReporter(CiRunService runs, CiAttemptService attempts, CiPlanCache plans, CiStatusDelivery delivery, CiStatusOptions options)
{
    public const string Context = "olve/preview";

    public void Report(Preview preview)
    {
        if (!runs.TryGet(preview.DeployedBy, out var deployed) || For(preview, deployed) is not { } status)
            return;
        delivery.Offer(new CiStatusKey(preview.PipelineId, deployed.HeadSha, Context), status);
    }

    internal CiCommitStatus? For(Preview preview, CiRun deployed)
    {
        var run = CiStatusPlanner.Link(options, preview.PipelineId, deployed.Number);
        return preview.State switch
        {
            PreviewState.Deploying => new CiCommitStatus("pending", "Deploying the preview", run, Context),
            PreviewState.Live when DeployFailed(deployed) => new CiCommitStatus("error", "Deploy failed, see olve/ci", run, Context),
            PreviewState.Live => new CiCommitStatus("success", preview.Url is null ? "Preview deployed" : "Preview is live", preview.Url ?? run, Context),
            PreviewState.Removing or PreviewState.Failing => new CiCommitStatus("pending", "Removing the preview", run, Context),
            PreviewState.Removed => new CiCommitStatus("success", "Preview removed", null, Context),
            _ => null,
        };
    }

    private bool DeployFailed(CiRun run) =>
        !plans.Get(run).TryPickProblems(out _, out var plan)
        && attempts.ListByRun(run.Id).GroupBy(a => a.StepName)
            .Any(g => plan.TryGetUnit(g.Key, out _, out var unit) && unit.Step.NeverReused && g.Last().Status is AttemptStatus.Failed);
}

/// <summary>Binding warnings for previews whose close runs keep failing. Stateless query; transient.</summary>
public class PreviewWarnings(PreviewService previews)
{
    public IReadOnlyList<string> For(Id<Pipeline> pipelineId) =>
    [
        .. previews.List().Where(p => p.PipelineId == pipelineId && p.State is PreviewState.Failing).OrderBy(p => p.Pull)
            .Select(p => p.RetryAt is null
                ? $"The preview of pull request #{p.Pull} can't be removed: the CI run that deployed it is gone."
                : $"The preview of pull request #{p.Pull} failed to be removed {p.Failures} time(s); it is tried again while the pull request stays closed."),
    ];
}
