using static Olve.Pipelines.Jobs.Job;
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

            if (IsBusy(job, busyKeys))
            {
                if (job.Status is Scheduled)
                {
                    logger.LogDebug(
                        "Holding Scheduled job '{JobId}' — another job is already InProgress for the same (pipeline, step) key",
                        job.Id);
                }
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

    private static BusyKeys CollectInProgressKeys(IEnumerable<Job> jobs)
    {
        var production = new HashSet<ProductionJob.ProductionJobKey>();
        var processing = new HashSet<ProcessingJob.ProcessingJobKey>();

        foreach (var job in jobs)
        {
            if (job.Status is not InProgress) continue;
            switch (job)
            {
                case ProductionJob p: production.Add(p.JobKey); break;
                case ProcessingJob p: processing.Add(p.JobKey); break;
            }
        }

        return new BusyKeys(production, processing);
    }

    private static bool IsBusy(Job job, BusyKeys busyKeys) => job switch
    {
        ProductionJob p => busyKeys.Production.Contains(p.JobKey),
        ProcessingJob p => busyKeys.Processing.Contains(p.JobKey),
        _ => false,
    };

    private static void ReserveKey(Job job, BusyKeys busyKeys)
    {
        switch (job)
        {
            case ProductionJob p: busyKeys.Production.Add(p.JobKey); break;
            case ProcessingJob p: busyKeys.Processing.Add(p.JobKey); break;
        }
    }

    private readonly record struct BusyKeys(
        HashSet<ProductionJob.ProductionJobKey> Production,
        HashSet<ProcessingJob.ProcessingJobKey> Processing);
}
