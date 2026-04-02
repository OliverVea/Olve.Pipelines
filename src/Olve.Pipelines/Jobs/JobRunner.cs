using Olve.Pipelines.Shared;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobRunner(JobService jobService, TimeProvider timeProvider, ILogger<JobRunner> logger)
{
    public void HandleJobAdded(Id<Job> jobId)
    {
        if (!jobService.TryGetJob<Job>(jobId, out var job))
            return;

        if (job.Status is not Scheduled)
            return;

        var now = timeProvider.GetUtcNow();

        var startResult = jobService.UpdateJob<Job>(jobId, j => j with { Status = new InProgress(now) });
        if (startResult.TryPickProblems(out var problems))
        {
            logger.LogWarning("Failed to start job '{JobId}': {Problems}", jobId, problems);
            return;
        }

        logger.LogInformation("Job '{JobId}' started", jobId);

        // No-op: immediately complete
        var completeResult = jobService.UpdateJob<Job>(jobId, j => j with { Status = new Done(now, timeProvider.GetUtcNow()) });
        if (completeResult.TryPickProblems(out var completeProblems))
        {
            logger.LogWarning("Failed to complete job '{JobId}': {Problems}", jobId, completeProblems);
            return;
        }

        logger.LogInformation("Job '{JobId}' completed", jobId);
    }
}
