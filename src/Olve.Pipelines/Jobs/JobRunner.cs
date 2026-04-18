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
        var jobIds = GetActiveJobIds();

        foreach (var jobId in jobIds)
        {
            if (registry.IsRunning(jobId)) continue;
            if (registry.ActiveCount >= MaxConcurrentJobs) return;

            using var scope = sp.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
            executor.EnsureRunning(jobId);
        }
    }

    private List<Id<Job>> GetActiveJobIds()
    {
        using var scope = sp.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        return queue.GetActiveJobIds().ToList();
    }
}
