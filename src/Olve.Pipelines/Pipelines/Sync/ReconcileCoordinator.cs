using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Orchestrates a single reconcile with the active-drain concurrency model:
/// <b>pause → drain → mutate → resume</b>.
///
/// <list type="number">
/// <item><b>Pause</b> — flag the pipeline so the trigger layer refuses new runs.
/// In-flight work runs to completion on its original config.</item>
/// <item><b>Drain</b> — wait (no lock held, no I/O) until the pipeline is quiescent: no job in
/// Scheduled/InProgress attempt. A generous timeout prevents a stuck attempt from wedging the pipeline.</item>
/// <item><b>Mutate</b> — under the global config lock, apply the change. At this point there are
/// zero non-terminal jobs, so reconcile can never change config out from under a running job.</item>
/// <item><b>Resume</b> — always clear the pause flag (success, failure, or timeout).</item>
/// </list>
/// The mutation itself is supplied by the caller; v2's version-based reconcile (B4+) plugs in here.
/// </summary>
public class ReconcileCoordinator(
    ReconcilePauseState pauseState,
    AttemptService attempts,
    ReconcileOptions options,
    TimeProvider timeProvider,
    ILogger<ReconcileCoordinator> logger)
{
    public async Task<Result> ReconcileAsync(Id<Pipeline> pipelineId, Func<Result> mutate, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        pauseState.Pause(pipelineId);
        try
        {
            if (!await DrainAsync(pipelineId, ct))
            {
                logger.LogWarning(
                    "Reconcile aborted: pipeline '{PipelineId}' did not drain within {Timeout} ({ElapsedMs}ms); will retry next poll",
                    pipelineId, options.DrainTimeout, sw.ElapsedMilliseconds);
                return new ResultProblem($"Pipeline '{pipelineId}' did not drain within {options.DrainTimeout}.");
            }

            Result result;
            lock (pauseState.MutationLock)
            {
                result = mutate();
            }

            if (result.TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Warning, problems,
                    "Reconcile for pipeline '{PipelineId}' failed after {ElapsedMs}ms", pipelineId, sw.ElapsedMilliseconds);
            else
                logger.LogInformation("Reconcile for pipeline '{PipelineId}' completed in {ElapsedMs}ms",
                    pipelineId, sw.ElapsedMilliseconds);

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

        while (attempts.HasActiveAttempts(pipelineId))
        {
            if (timeProvider.GetUtcNow() >= deadline)
                return false;

            await Task.Delay(options.DrainPollInterval, timeProvider, ct);
        }

        return true;
    }
}
