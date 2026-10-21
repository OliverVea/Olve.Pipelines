using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobRunner(
    IServiceProvider sp,
    JobWatcherRegistry registry,
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

            await Task.Delay(PollInterval, ct);
        }
    }

    private void DispatchTick()
    {
        var jobs = GetActiveJobs();
        var busyKeys = CollectInProgressKeys(jobs);

        foreach (var job in jobs)
        {
            if (registry.IsRunning(job.Id)) continue;
            if (registry.ActiveCount >= MaxConcurrentJobs) return;

            // The busy-key gate holds a Scheduled job behind an in-flight job for the same
            // (pipeline, step). It must NOT apply to an InProgress job: after a restart its watcher is
            // gone but it still owns its key (CollectInProgressKeys added it), so gating it here would
            // make it block itself forever and never reattach to its K8s Job — wedging the pipeline.
            if (job.Status is Scheduled && IsBusy(job, busyKeys))
            {
                logger.LogDebug(
                    "Holding Scheduled job '{JobId}' — another job is already InProgress for the same (pipeline, step) key",
                    job.Id);
                continue;
            }

            ReserveKey(job, busyKeys);

            using var scope = sp.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
            executor.EnsureRunning(job.Id);
        }
    }

    private List<Job> GetActiveJobs()
    {
        using var scope = sp.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        return queue.GetActiveJobs().ToList();
    }

    private static HashSet<JobKey> CollectInProgressKeys(IEnumerable<Job> jobs)
        => jobs.Where(j => j.Status is InProgress).Select(j => j.Key).ToHashSet();

    private static bool IsBusy(Job job, HashSet<JobKey> busyKeys) => busyKeys.Contains(job.Key);

    private static void ReserveKey(Job job, HashSet<JobKey> busyKeys) => busyKeys.Add(job.Key);
}
