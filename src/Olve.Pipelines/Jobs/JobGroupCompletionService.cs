using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobGroupCompletionService(
    JobService jobService,
    JobGroupService jobGroupService,
    ArtifactBundleService artifactBundleService,
    JobEvents jobEvents,
    ILogger<JobGroupCompletionService> logger)
{
    public void HandleJobUpdated(Id<Job> jobId)
    {
        if (!jobService.TryGetJob<Job>(jobId, out var job))
            return;

        if (!job.Status.IsTerminal())
            return;

        var groupJobs = jobService.GetJobsByGroup(job.JobGroupId);

        if (groupJobs.Any(j => !j.Status.IsTerminal()))
            return;

        var allSucceeded = groupJobs.All(j => j.Status is Done);

        if (!jobGroupService.TryGet(job.JobGroupId, out var group) || group is null)
        {
            logger.LogWarning("JobGroup '{GroupId}' not found for job '{JobId}'", job.JobGroupId, jobId);
            return;
        }

        if (allSucceeded)
        {
            if (group is ProductionJobGroup production)
                artifactBundleService.UpdateStatus(production.ArtifactBundleId, ArtifactBundleStatus.Completed);

            logger.LogInformation("JobGroup '{GroupId}' completed successfully", group.Id);
            jobEvents.OnGroupCompleted.Invoke(group.Id);
        }
        else
        {
            if (group is ProductionJobGroup production)
                artifactBundleService.UpdateStatus(production.ArtifactBundleId, ArtifactBundleStatus.Failed);

            logger.LogWarning("JobGroup '{GroupId}' failed", group.Id);
            jobEvents.OnGroupFailed.Invoke(group.Id);
        }
    }
}
