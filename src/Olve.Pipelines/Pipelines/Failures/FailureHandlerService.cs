using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Failures;

/// <summary>Where failure-handler links point (<c>Links:BaseUrl</c>, else <c>Webhooks:PublicBaseUrl</c>; empty = relative links).</summary>
public sealed record FailureHandlerOptions(string LinkBaseUrl);

/// <summary>
/// Starts failure-handler runs (async I/O seam). <see cref="Launch"/> must not block: it is called from the
/// run-status event, under the scheduling lock.
/// </summary>
public interface IFailureHandlerLauncher
{
    void Launch(FailureHandlerDispatch dispatch);
}

/// <summary>Default without Kubernetes: logs what would run.</summary>
public sealed class NoOpFailureHandlerLauncher(ILogger<NoOpFailureHandlerLauncher> logger) : IFailureHandlerLauncher
{
    public void Launch(FailureHandlerDispatch dispatch)
        => logger.LogInformation(
            "Kubernetes is not configured; not running failure handler '{HandlerName}' for run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}')",
            dispatch.Handler.Name, dispatch.Run.Number, dispatch.Pipeline.Name, dispatch.Pipeline.Id);
}

/// <summary>
/// When a run fails (Running → Failed): collects its failed steps (each step's latest attempt that failed,
/// not stale relative to <see cref="Run.ResumedAt"/>), plans the pinned manifest's failure handlers
/// (<see cref="FailureHandlerPlanner"/>) and hands each dispatch to <see cref="IFailureHandlerLauncher"/>.
/// Handlers are untracked and best-effort: no attempt, no retry, and a handler's own outcome never feeds
/// back into the run. A run that failed without a failed step (unusable manifest, malformed input) runs
/// no handler. Sync domain rule service (transient).
/// </summary>
public class FailureHandlerService(
    EntityStore<Pipeline> pipelines,
    AttemptService attempts,
    PipelineVersionService versions,
    RunPlanCache plans,
    FailureHandlerOptions options,
    IFailureHandlerLauncher launcher,
    ILogger<FailureHandlerService> logger)
{
    /// <summary>Never throws: a failure-handler problem must not disturb the scheduler that failed the run.</summary>
    public void HandleRunFailed(Run run)
    {
        try
        {
            Dispatch(run);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not dispatch failure handlers for run #{RunNumber} of pipeline '{PipelineId}'", run.Number, run.PipelineId);
        }
    }

    private void Dispatch(Run run)
    {
        if (run.Status is not RunStatus.Failed)
            return;
        if (!pipelines.TryGet(run.PipelineId, out var pipeline)
            || !versions.TryGet(run.VersionId, out var version)
            || plans.Get(run.VersionId).TryPickProblems(out _, out var plan))
            return;
        if (plan.Manifest.FailureHandlers.Count == 0)
            return;

        var failed = new List<Attempt>();
        foreach (var step in plan.Stages.SelectMany(s => s.Steps))
        {
            var latest = attempts.ListForStep(run.Id, step.Name).LastOrDefault();
            if (latest?.Status is AttemptStatus.Failed && !(run.ResumedAt is { } resumed && latest.CreatedAt < resumed))
                failed.Add(latest);
        }

        if (failed.Count == 0)
        {
            logger.LogInformation("Run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}') failed without a failed step; no failure handler runs",
                run.Number, pipeline.Name, pipeline.Id);
            return;
        }

        var (dispatches, skipped) = FailureHandlerPlanner.Plan(pipeline, run, version, plan.Manifest, failed, options.LinkBaseUrl);
        foreach (var skip in skipped)
            logger.LogWarning("Failure handler '{HandlerName}' of run #{RunNumber}, pipeline '{PipelineName}' ('{PipelineId}') not run: {Reason}",
                skip.Handler, run.Number, pipeline.Name, pipeline.Id, skip.Reason);

        foreach (var dispatch in dispatches)
        {
            logger.LogInformation("Dispatching failure handler '{HandlerName}' (Job '{JobName}') for run #{RunNumber} of pipeline '{PipelineName}' ('{PipelineId}'), failed steps {FailedSteps}",
                dispatch.Handler.Name, dispatch.JobName, run.Number, pipeline.Name, pipeline.Id, string.Join(", ", dispatch.FailedSteps));
            launcher.Launch(dispatch);
        }
    }
}

/// <summary>One subscription: a run reaching Failed dispatches its failure handlers.</summary>
public class FailureHandlerEventRegistration(RunEvents runEvents, IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        runEvents.OnUpdated.Subscribe(e =>
        {
            if (e.Before.Status is RunStatus.Running && e.After.Status is RunStatus.Failed)
                sp.GetRequiredService<FailureHandlerService>().HandleRunFailed(e.After);
        });

        return Result.Success();
    }
}
