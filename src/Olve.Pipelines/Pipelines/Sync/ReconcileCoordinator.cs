using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Orchestrates a single reconcile with the active-drain concurrency model:
/// <b>pause → drain → mutate → resume</b>.
///
/// <list type="number">
/// <item><b>Pause</b> — flag the pipeline so the trigger layer refuses new production runs.
/// In-flight chains keep promoting to completion on their original config.</item>
/// <item><b>Drain</b> — wait (no lock held, no I/O) until the pipeline is quiescent: no job in
/// Scheduled/InProgress. A generous timeout prevents a stuck job from wedging the pipeline.</item>
/// <item><b>Mutate</b> — under the global config lock, apply the diff. At this point there are
/// zero non-terminal jobs, so reconcile can never delete a step out from under a running job.</item>
/// <item><b>Resume</b> — always clear the pause flag (success, failure, or timeout).</item>
/// </list>
/// </summary>
public class ReconcileCoordinator(
    ReconcilePauseState pauseState,
    PipelineReconciler reconciler,
    JobService jobs,
    ReconcileOptions options,
    TimeProvider timeProvider,
    ILogger<ReconcileCoordinator> logger)
{
    public async Task<Result> ReconcileAsync(Id<Pipeline> pipelineId, PipelineManifest desired, CancellationToken ct = default)
    {
        pauseState.Pause(pipelineId);
        try
        {
            if (!await DrainAsync(pipelineId, ct))
            {
                logger.LogWarning(
                    "Reconcile aborted: pipeline '{PipelineId}' did not drain within {Timeout}; will retry next poll",
                    pipelineId, options.DrainTimeout);
                return new ResultProblem($"Pipeline '{pipelineId}' did not drain within {options.DrainTimeout}.");
            }

            Result result;
            lock (pauseState.MutationLock)
            {
                result = reconciler.Reconcile(pipelineId, desired);
            }

            return result;
        }
        finally
        {
            pauseState.Resume(pipelineId);
        }
    }

    private async Task<bool> DrainAsync(Id<Pipeline> pipelineId, CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + options.DrainTimeout;

        while (jobs.HasActiveJobs(pipelineId))
        {
            if (timeProvider.GetUtcNow() >= deadline)
                return false;

            await Task.Delay(options.DrainPollInterval, timeProvider, ct);
        }

        return true;
    }
}
