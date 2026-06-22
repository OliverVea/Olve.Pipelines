using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobCancellationService(JobService jobService, ILogger<JobCancellationService> logger)
{
    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        var jobs = jobService.ListJobs()
            .Where(j => j.PipelineId == pipelineId && j.Status is Scheduled or InProgress);

        foreach (var job in jobs)
        {
            var result = jobService.CancelJob(job.Id);
            if (result.TryPickProblems(out var problems))
            {
                logger.LogProblems(LogLevel.Warning, problems,
                    "Failed to cancel job '{JobId}' when pipeline '{PipelineId}' was deleted",
                    job.Id, pipelineId);
            }
        }
    }
}
