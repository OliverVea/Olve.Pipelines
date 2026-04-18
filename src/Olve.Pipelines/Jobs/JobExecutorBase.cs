using Microsoft.Extensions.Hosting;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public abstract class JobExecutorBase(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    ILogger logger) : IJobExecutor
{
    public void EnsureRunning(Id<Job> id)
    {
        registry.EnsureRunning(id, async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
            var jobService = scope.ServiceProvider.GetRequiredService<JobService>();

            if (!jobService.TryGetJob<Job>(id, out var job))
            {
                logger.LogDebug("Watcher for job '{JobId}' exiting: job not found", id);
                return;
            }

            if (job.Status is not (Scheduled or InProgress))
            {
                logger.LogDebug("Watcher for job '{JobId}' exiting: status is {Status}", id, job.Status.GetType().Name);
                return;
            }

            if (executor is not JobExecutorBase typedExecutor)
            {
                throw new InvalidOperationException(
                    $"Resolved executor '{executor.GetType().Name}' is not a JobExecutorBase.");
            }

            try
            {
                await typedExecutor.RunToCompletionAsync(job, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                logger.LogInformation("Watcher for job '{JobId}' cancelled during shutdown — will reattach on next start", id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in watcher for job '{JobId}'", id);
            }
        });
    }

    protected internal abstract Task RunToCompletionAsync(Job job, CancellationToken ct);
}
