using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

public class JobRunner(
    IServiceProvider sp,
    JobWatcherRegistry registry,
    JobDispatchSignal signal,
    ILogger<JobRunner> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public const int DefaultMaxConcurrentJobs = 4;

    /// <summary>Step Jobs running at once, across all pipelines (<c>Jobs:MaxConcurrent</c>).</summary>
    public int MaxConcurrentJobs { get; init; } = DefaultMaxConcurrentJobs;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Job runner started: at most {MaxConcurrentJobs} step Jobs at once", MaxConcurrentJobs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DispatchTick();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in JobRunner dispatch tick");
            }

            await signal.WaitAsync(PollInterval, ct);
        }
    }

    internal void DispatchTick()
    {
        var attempts = GetActiveAttempts();
        var busyKeys = CollectInProgressKeys(attempts);
        var cellsInFlight = attempts
            .Where(a => a.Status is InProgress || registry.IsRunning(a.Id))
            .GroupBy(MatrixParallelism.GroupOf)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var attempt in attempts)
        {
            if (attempt.Status is Waiting) continue;
            if (registry.IsRunning(attempt.Id)) continue;
            if (registry.ActiveCount >= MaxConcurrentJobs) return;

            // The busy-key gate holds a Scheduled attempt behind an in-flight attempt for the same
            // (pipeline, step). It must NOT apply to an InProgress attempt: after a restart its watcher is
            // gone but it still owns its key (CollectInProgressKeys added it), so gating it here would
            // make it block itself forever and never reattach to its K8s Job — wedging the pipeline.
            if (attempt.Status is Scheduled && IsBusy(attempt, busyKeys))
            {
                logger.LogDebug(
                    "Holding Scheduled attempt '{AttemptId}' of step '{StepName}' — another attempt is already InProgress for the same (pipeline, step) key",
                    attempt.Id, attempt.StepName);
                continue;
            }

            using var scope = sp.CreateScope();
            if (attempt.Status is Scheduled && MatrixCells.TryParseName(attempt.StepName, out _)
                && scope.ServiceProvider.GetRequiredService<MatrixParallelism>().LimitOf(attempt) is { } limit)
            {
                var group = MatrixParallelism.GroupOf(attempt);
                var running = cellsInFlight.GetValueOrDefault(group);
                if (running >= limit)
                {
                    logger.LogDebug("Holding Scheduled attempt '{AttemptId}' of cell '{StepName}': {Running} of its step's cells are in progress (maxParallel {MaxParallel})",
                        attempt.Id, attempt.StepName, running, limit);
                    continue;
                }
                cellsInFlight[group] = running + 1;
            }

            ReserveKey(attempt, busyKeys);

            var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
            executor.EnsureRunning(attempt.Id);
        }
    }

    private List<Attempt> GetActiveAttempts()
    {
        using var scope = sp.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        return queue.GetActiveAttempts().ToList();
    }

    private static HashSet<StepKey> CollectInProgressKeys(IEnumerable<Attempt> attempts)
        => attempts.Where(j => j.Status is InProgress).Select(j => j.Key).ToHashSet();

    private static bool IsBusy(Attempt attempt, HashSet<StepKey> busyKeys) => busyKeys.Contains(attempt.Key);

    private static void ReserveKey(Attempt attempt, HashSet<StepKey> busyKeys) => busyKeys.Add(attempt.Key);
}
