using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.Job;

namespace Olve.Pipelines.Jobs;

public class NoOpJobExecutor(ArtifactBundleService artifactBundleService, ILogger<NoOpJobExecutor> logger) : IJobExecutor
{
    public Task<JobExecutionResult> ExecuteAsync(Job job, CancellationToken ct)
    {
        logger.LogInformation("No-op execution for job '{JobId}'", job.Id);

        JobExecutionResult result = job switch
        {
            ProductionJob productionJob => new JobExecutionResult.Success(
                artifactBundleService.Create(productionJob.PipelineId).Id),
            ProcessingJob => new JobExecutionResult.Success(),
            _ => new JobExecutionResult.Failure($"Unknown job type: {job.GetType().Name}"),
        };

        return Task.FromResult(result);
    }
}
