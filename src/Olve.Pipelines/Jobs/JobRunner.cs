using Olve.Pipelines.Shared;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobRunner(IServiceProvider sp, ILogger<JobRunner> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ProcessQueueAsync(ct);
            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        var jobService = scope.ServiceProvider.GetRequiredService<JobService>();
        var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var jobIds = queue.GetQueuedJobIds().ToList();
        foreach (var jobId in jobIds)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessJobAsync(jobId, jobService, executor, time, ct);
        }
    }

    private async Task ProcessJobAsync(
        Id<Job> jobId,
        JobService jobService,
        IJobExecutor executor,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!jobService.TryGetJob<Job>(jobId, out var job)) return;
        if (job.Status is not Scheduled) return;

        var startedAt = time.GetUtcNow();
        var startResult = jobService.UpdateJob<Job>(jobId, j => j with { Status = new InProgress(startedAt) });
        if (startResult.TryPickProblems(out var startProblems))
        {
            logger.LogWarning("Failed to start job '{JobId}': {Problems}", jobId, startProblems);
            return;
        }

        logger.LogInformation("Job '{JobId}' started", jobId);

        // Re-fetch to get updated job
        if (!jobService.TryGetJob<Job>(jobId, out job)) return;

        try
        {
            var result = await executor.ExecuteAsync(job, ct);
            var completedAt = time.GetUtcNow();

            switch (result)
            {
                case JobExecutionResult.Success success:
                    CompleteJob(jobId, job, jobService, success, startedAt, completedAt);
                    break;
                case JobExecutionResult.Failure failure:
                    FailJob(jobId, jobService, startedAt, completedAt, failure.Reason);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception executing job '{JobId}'", jobId);
            FailJob(jobId, jobService, startedAt, time.GetUtcNow(), ex.Message);
        }
    }

    private void CompleteJob(
        Id<Job> jobId,
        Job job,
        JobService jobService,
        JobExecutionResult.Success success,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        switch (job)
        {
            case ProductionJob:
                var bundleId = success.ArtifactBundleId
                    ?? throw new InvalidOperationException("Production job must produce an artifact bundle");
                jobService.UpdateJob<ProductionJob>(jobId, j => j with
                {
                    Status = new Done(startedAt, completedAt),
                    ProductionResult = bundleId,
                });
                break;
            case ProcessingJob:
                jobService.UpdateJob<ProcessingJob>(jobId, j => j with
                {
                    Status = new Done(startedAt, completedAt),
                    ProcessingResult = Result.Success(),
                });
                break;
        }

        logger.LogInformation("Job '{JobId}' completed", jobId);
    }

    private void FailJob(
        Id<Job> jobId,
        JobService jobService,
        DateTimeOffset startedAt,
        DateTimeOffset failedAt,
        string? reason)
    {
        jobService.UpdateJob<Job>(jobId, j => j with
        {
            Status = new Failed(startedAt, failedAt, reason),
        });

        logger.LogWarning("Job '{JobId}' failed: {Reason}", jobId, reason);
    }
}
