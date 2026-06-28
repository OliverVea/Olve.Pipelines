using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobGroupCompletionService(
    JobService jobService,
    JobGroupService jobGroupService,
    ArtifactBundleService artifactBundleService,
    JobGroupCompletionTracker completionTracker,
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

        // Fire-once: when the last few jobs of a group reach a terminal state concurrently, each of
        // their watcher threads observes the whole group terminal here. Exactly one wins the claim
        // and raises completion — so a multi-step production group promotes its bundle into the first
        // processing step once, not once per parallel job (which deadlocks on mutual supersession).
        if (!completionTracker.TryClaimCompletion(job.JobGroupId))
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
