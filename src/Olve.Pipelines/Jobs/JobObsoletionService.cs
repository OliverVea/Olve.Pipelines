using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobObsoletionService(JobService jobService, ILogger<JobObsoletionService> logger)
{
    public void HandleJobAdded(Id<Job> jobId)
    {
        if (!jobService.TryGetJob<Job>(jobId, out var newJob))
            return;

        var existingJob = jobService.ListJobs()
            .FirstOrDefault(j => j.Id != newJob.Id && j.Status is Scheduled && HasSameKey(j, newJob));

        if (existingJob is null)
            return;

        var result = jobService.UpdateJob<Job>(existingJob.Id, j => j with { Status = new Obsolete(newJob.Id) });
        if (result.TryPickProblems(out var problems))
        {
            logger.LogWarning(
                "Failed to obsolete job '{ExistingJobId}' when superseded by '{NewJobId}': {Problems}",
                existingJob.Id, newJob.Id, problems);
        }
    }

    private static bool HasSameKey(Job a, Job b) => (a, b) switch
    {
        (ProductionJob pa, ProductionJob pb) => pa.JobKey == pb.JobKey,
        (ProcessingJob pa, ProcessingJob pb) => pa.JobKey == pb.JobKey,
        _ => false
    };
}
