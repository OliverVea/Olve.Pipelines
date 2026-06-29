using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class NoOpJobExecutor(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    NoOpJobExecutorPendingStore pendingStore,
    JobService jobService,
    TimeProvider time,
    ILogger<NoOpJobExecutor> logger)
    : JobExecutorBase(scopeFactory, registry, lifetime, logger)
{
    protected internal override async Task RunToCompletionAsync(Job job, CancellationToken ct)
    {
        logger.LogInformation("No-op execution for job '{JobId}', awaiting Finish", job.Id);

        var startedAt = time.GetUtcNow();
        if (job.Status is Scheduled)
        {
            LogIfFailed(jobService.UpdateJob<Job>(job.Id, j => j with { Status = new InProgress(startedAt) }), job.Id);
        }
        else if (job.Status is InProgress ip)
        {
            startedAt = ip.StartedAt;
        }

        var tcs = pendingStore.Register(job.Id);

        NoOpJobResult result;
        await using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            result = await tcs.Task;
        }

        var completedAt = time.GetUtcNow();
        switch (result)
        {
            case NoOpJobResult.Success:
                WriteDone(job, startedAt, completedAt);
                break;
            case NoOpJobResult.Failure failure:
                LogIfFailed(jobService.UpdateJob<Job>(job.Id, j => j with
                {
                    Status = new Failed(startedAt, completedAt, failure.Reason),
                }), job.Id);
                break;
        }
    }

    private void WriteDone(Job job, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        LogIfFailed(jobService.UpdateJob<Job>(job.Id, j => j with
        {
            Status = new Done(startedAt, completedAt),
        }), job.Id);
    }

    private void LogIfFailed(Result result, Id<Job> jobId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of job '{JobId}' (it may have been deleted)", jobId);
    }
}
