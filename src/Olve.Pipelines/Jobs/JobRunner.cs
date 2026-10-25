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

    public int MaxConcurrentJobs { get; init; } = 4;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
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

    private void DispatchTick()
    {
        var attempts = GetActiveAttempts();
        var busyKeys = CollectInProgressKeys(attempts);

        foreach (var attempt in attempts)
        {
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

            ReserveKey(attempt, busyKeys);

            using var scope = sp.CreateScope();
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
