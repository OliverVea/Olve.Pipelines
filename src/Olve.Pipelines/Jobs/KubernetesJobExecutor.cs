using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using static Olve.Pipelines.Jobs.Job;

namespace Olve.Pipelines.Jobs;

public class KubernetesJobExecutor(
    KubernetesClient kubernetesClient,
    KubernetesOptions options,
    ProductionStepService productionStepService,
    ProcessingStepService processingStepService,
    JobGroupService jobGroupService,
    IPipelineSecretStore secretStore,
    ILogger<KubernetesJobExecutor> logger) : IJobExecutor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public async Task<JobExecutionResult> ExecuteAsync(Job job, CancellationToken ct)
    {
        return job switch
        {
            ProductionJob pj => await ExecuteProductionJobAsync(pj, ct),
            ProcessingJob cj => await ExecuteProcessingJobAsync(cj, ct),
            _ => new JobExecutionResult.Failure($"Unknown job type: {job.GetType().Name}"),
        };
    }

    private async Task<JobExecutionResult> ExecuteProductionJobAsync(ProductionJob job, CancellationToken ct)
    {
        var configResult = productionStepService.TryGetConfiguration(job.ProductionStepId);
        if (configResult.TryPickProblems(out var problems, out var config))
            return new JobExecutionResult.Failure($"Missing configuration for production step '{job.ProductionStepId}': {problems}");

        var stepResult = productionStepService.TryGet(job.ProductionStepId);
        if (stepResult.TryPickProblems(out problems, out var step))
            return new JobExecutionResult.Failure($"Production step '{job.ProductionStepId}' not found: {problems}");

        if (!jobGroupService.TryGet(job.JobGroupId, out var group) || group is not ProductionJobGroup productionGroup)
            return new JobExecutionResult.Failure($"Production job group '{job.JobGroupId}' not found.");

        var secretName = await GetSecretNameIfExists(job.PipelineId, ct);

        var spec = new KubernetesJobSpec(
            Name: JobName(job.Id),
            Image: config.Image,
            Script: config.Script,
            EnvironmentVariables: config.EnvironmentVariables,
            SecretName: secretName,
            OutputBundleS3Key: $"bundles/{productionGroup.ArtifactBundleId.Value.Value:N}/{Sanitize(step.Name)}");

        return await CreateAndPollAsync(spec, ct);
    }

    private async Task<JobExecutionResult> ExecuteProcessingJobAsync(ProcessingJob job, CancellationToken ct)
    {
        var configResult = processingStepService.TryGetConfiguration(job.ProcessingStepId);
        if (configResult.TryPickProblems(out var problems, out var config))
            return new JobExecutionResult.Failure($"Missing configuration for processing step '{job.ProcessingStepId}': {problems}");

        var stepResult = processingStepService.TryGet(job.ProcessingStepId);
        if (stepResult.TryPickProblems(out problems, out var step))
            return new JobExecutionResult.Failure($"Processing step '{job.ProcessingStepId}' not found: {problems}");

        var secretName = await GetSecretNameIfExists(job.PipelineId, ct);

        var spec = new KubernetesJobSpec(
            Name: JobName(job.Id),
            Image: config.Image,
            Script: config.Script,
            EnvironmentVariables: config.EnvironmentVariables,
            SecretName: secretName,
            InputBundleS3Key: $"bundles/{job.ArtifactBundleId.Value.Value:N}",
            OutputBundleS3Key: $"bundles/{job.ArtifactBundleId.Value.Value:N}/processing/{Sanitize(step.Name)}");

        return await CreateAndPollAsync(spec, ct);
    }

    private async Task<JobExecutionResult> CreateAndPollAsync(KubernetesJobSpec spec, CancellationToken ct)
    {
        logger.LogInformation("Creating K8s Job '{JobName}'", spec.Name);
        await kubernetesClient.CreateJobAsync(options.Namespace, spec, ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);

            var status = await kubernetesClient.GetJobStatusAsync(options.Namespace, spec.Name, ct);

            switch (status.Phase)
            {
                case JobPhase.Succeeded:
                    logger.LogInformation("K8s Job '{JobName}' succeeded", spec.Name);
                    return new JobExecutionResult.Success();
                case JobPhase.Failed:
                    logger.LogWarning("K8s Job '{JobName}' failed: {Message}", spec.Name, status.Message);
                    return new JobExecutionResult.Failure(status.Message ?? "K8s job failed");
            }
        }

        ct.ThrowIfCancellationRequested();
        return new JobExecutionResult.Failure("Unreachable");
    }

    private async Task<string?> GetSecretNameIfExists(Id<Pipeline> pipelineId, CancellationToken ct)
    {
        var hasSecrets = await secretStore.HasSecretsAsync(pipelineId, ct);
        return hasSecrets ? $"olve-pipeline-{pipelineId.Value.Value:N}" : null;
    }

    private static string JobName(Id<Job> jobId)
    {
        var name = $"olve-{jobId.Value.Value:N}";
        return name.Length > 63 ? name[..63] : name;
    }

    private static string Sanitize(string name)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "-").Trim('-');
        return sanitized.Length > 30 ? sanitized[..30].TrimEnd('-') : sanitized;
    }
}
