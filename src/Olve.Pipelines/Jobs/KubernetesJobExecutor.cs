using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
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
    ICredentialsProvider<S3Credentials> s3CredentialsProvider,
    IAmazonS3 s3,
    StorageOptions storageOptions,
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

        if (!jobGroupService.TryGet(job.JobGroupId, out var group) || group is not ProductionJobGroup productionGroup)
            return new JobExecutionResult.Failure($"Production job group '{job.JobGroupId}' not found.");

        var secretName = await GetSecretNameIfExists(job.PipelineId, ct);
        var s3SecretName = await CreateS3CredentialsSecretAsync(job.Id, ct);
        var prefix = S3Prefix(job.PipelineId, productionGroup.ArtifactBundleId);

        var spec = new KubernetesJobSpec(
            Name: JobName(job.Id),
            Image: config.Image,
            Script: config.Script,
            OutputBundleS3Prefix: $"{prefix}/production/{job.ProductionStepId.Value.Value:N}",
            S3HelperImage: options.S3HelperImage,
            S3Bucket: options.S3Bucket,
            S3Endpoint: options.S3Endpoint,
            S3CredentialsSecretName: s3SecretName,
            S3SkipCertValidation: options.S3SkipCertValidation,
            EnvironmentVariables: config.EnvironmentVariables,
            SecretName: secretName);

        var logKey = LogS3Key(job.PipelineId, productionGroup.ArtifactBundleId, job.Id);
        return await CreatePollAndCleanupAsync(spec, s3SecretName, logKey, ct);
    }

    private async Task<JobExecutionResult> ExecuteProcessingJobAsync(ProcessingJob job, CancellationToken ct)
    {
        var configResult = processingStepService.TryGetConfiguration(job.ProcessingStepId);
        if (configResult.TryPickProblems(out var problems, out var config))
            return new JobExecutionResult.Failure($"Missing configuration for processing step '{job.ProcessingStepId}': {problems}");

        var secretName = await GetSecretNameIfExists(job.PipelineId, ct);
        var s3SecretName = await CreateS3CredentialsSecretAsync(job.Id, ct);
        var prefix = S3Prefix(job.PipelineId, job.ArtifactBundleId);

        var spec = new KubernetesJobSpec(
            Name: JobName(job.Id),
            Image: config.Image,
            Script: config.Script,
            OutputBundleS3Prefix: $"{prefix}/processing/{job.ProcessingStepId.Value.Value:N}",
            S3HelperImage: options.S3HelperImage,
            S3Bucket: options.S3Bucket,
            S3Endpoint: options.S3Endpoint,
            S3CredentialsSecretName: s3SecretName,
            S3SkipCertValidation: options.S3SkipCertValidation,
            EnvironmentVariables: config.EnvironmentVariables,
            SecretName: secretName,
            InputBundleS3Prefix: $"{prefix}/production");

        var logKey = LogS3Key(job.PipelineId, job.ArtifactBundleId, job.Id);
        return await CreatePollAndCleanupAsync(spec, s3SecretName, logKey, ct);
    }

    private async Task<string> CreateS3CredentialsSecretAsync(Id<Job> jobId, CancellationToken ct)
    {
        var creds = await s3CredentialsProvider.GetCredentialsAsync(ct);
        var secretName = $"olve-s3-{jobId.Value.Value:N}";

        // Build MC_HOST_s3 value: https://ACCESS:SECRET[:TOKEN]@host
        var endpoint = new Uri(options.S3Endpoint);
        var authPart = creds.SessionToken is not null
            ? $"{creds.AccessKey}:{creds.SecretKey}:{creds.SessionToken}"
            : $"{creds.AccessKey}:{creds.SecretKey}";
        var mcHost = $"{endpoint.Scheme}://{authPart}@{endpoint.Authority}";

        var data = new Dictionary<string, string>
        {
            ["MC_HOST_s3"] = mcHost,
        };

        await kubernetesClient.CreateSecretAsync(options.Namespace, secretName, data, ct);
        logger.LogInformation("Created S3 credentials secret '{SecretName}'", secretName);

        return secretName;
    }

    private async Task<JobExecutionResult> CreatePollAndCleanupAsync(KubernetesJobSpec spec, string s3SecretName, string logKey, CancellationToken ct)
    {
        try
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
                        await UploadLogsAsync(spec.Name, logKey, ct);
                        return new JobExecutionResult.Success();
                    case JobPhase.Failed:
                        logger.LogWarning("K8s Job '{JobName}' failed: {Message}", spec.Name, status.Message);
                        await UploadLogsAsync(spec.Name, logKey, ct);
                        return new JobExecutionResult.Failure(status.Message ?? "K8s job failed");
                }
            }

            ct.ThrowIfCancellationRequested();
            return new JobExecutionResult.Failure("Unreachable");
        }
        finally
        {
            await CleanupS3SecretAsync(s3SecretName);
        }
    }

    private async Task UploadLogsAsync(string k8sJobName, string logKey, CancellationToken ct)
    {
        try
        {
            var logs = await kubernetesClient.GetPodLogsAsync(options.Namespace, k8sJobName, container: "runner", ct: ct);
            if (logs is null)
            {
                logger.LogWarning("No pod found for K8s Job '{JobName}' — cannot persist logs", k8sJobName);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(logs);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = logKey,
                InputStream = new MemoryStream(bytes),
                ContentType = "text/plain; charset=utf-8",
            }, ct);

            logger.LogInformation("Persisted logs for K8s Job '{JobName}' to '{LogKey}'", k8sJobName, logKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist logs for K8s Job '{JobName}' to '{LogKey}'", k8sJobName, logKey);
        }
    }

    private async Task CleanupS3SecretAsync(string secretName)
    {
        try
        {
            await kubernetesClient.DeleteSecretAsync(options.Namespace, secretName);
            logger.LogInformation("Deleted S3 credentials secret '{SecretName}'", secretName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up S3 credentials secret '{SecretName}'", secretName);
        }
    }

    private async Task<string?> GetSecretNameIfExists(Id<Pipeline> pipelineId, CancellationToken ct)
    {
        var hasSecrets = await secretStore.HasSecretsAsync(pipelineId, ct);
        return hasSecrets ? $"olve-pipeline-{pipelineId.Value.Value:N}" : null;
    }

    internal static string S3Prefix(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId)
        => $"bundles/{pipelineId.Value.Value:N}/{artifactBundleId.Value.Value:N}";

    internal static string LogS3Key(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId, Id<Job> jobId)
        => $"{S3Prefix(pipelineId, artifactBundleId)}/logs/{jobId.Value.Value:N}.log";

    private static string JobName(Id<Job> jobId)
    {
        var name = $"olve-{jobId.Value.Value:N}";
        return name.Length > 63 ? name[..63] : name;
    }
}
