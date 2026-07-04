using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class KubernetesJobExecutor(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    IKubernetesClient kubernetesClient,
    KubernetesOptions options,
    ProductionStepService productionStepService,
    ProcessingStepService processingStepService,
    JobGroupService jobGroupService,
    IPipelineSecretStore secretStore,
    ICredentialsProvider<S3Credentials> s3CredentialsProvider,
    IAmazonS3 s3,
    StorageOptions storageOptions,
    JobService jobService,
    TimeProvider time,
    ILogger<KubernetesJobExecutor> logger)
    : JobExecutorBase(scopeFactory, registry, lifetime, logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // A K8s-submission failure keeps the job Scheduled and is retried by JobRunner respawning the
    // watcher. Cap the attempts so a genuine outage fails the job loudly instead of looping forever.
    internal const int MaxSubmissionAttempts = 3; // initial attempt + 2 retries

    // Held between retries so they ride out a transient outage rather than hammering K8s at the
    // JobRunner tick rate. Overridable so tests don't wait real seconds.
    internal TimeSpan SubmissionRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    protected internal override async Task RunToCompletionAsync(Job job, CancellationToken ct)
    {
        switch (job)
        {
            case ProductionJob pj:
                await ExecuteProductionJobAsync(pj, ct);
                break;
            case ProcessingJob cj:
                await ExecuteProcessingJobAsync(cj, ct);
                break;
            default:
                FailJob(job, job.Status as InProgress, $"Unknown job type: {job.GetType().Name}");
                break;
        }
    }

    private async Task ExecuteProductionJobAsync(ProductionJob job, CancellationToken ct)
    {
        var configResult = productionStepService.TryGetConfiguration(job.ProductionStepId);
        if (configResult.TryPickProblems(out var problems, out var config))
        {
            FailJob(job, job.Status as InProgress, $"Missing configuration for production step '{job.ProductionStepId}': {problems}");
            return;
        }

        if (!jobGroupService.TryGet(job.JobGroupId, out var group) || group is not ProductionJobGroup productionGroup)
        {
            FailJob(job, job.Status as InProgress, $"Production job group '{job.JobGroupId}' not found.");
            return;
        }

        var stepName = productionStepService.TryGet(job.ProductionStepId).TryPickProblems(out _, out var step)
            ? "(unknown)"
            : step.Name;
        var logKey = LogS3Key(job.PipelineId, productionGroup.ArtifactBundleId, job.Id);

        await SubmitGuardedAsync(job, logKey, async token =>
        {
            var secretName = await GetSecretNameIfExists(job.PipelineId, token);
            var s3SecretName = await CreateS3CredentialsSecretAsync(job.Id, token);
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

            await SubmitOrReattachAsync(job, spec, stepName, s3SecretName, logKey, token);
        }, ct);
    }

    private async Task ExecuteProcessingJobAsync(ProcessingJob job, CancellationToken ct)
    {
        var configResult = processingStepService.TryGetConfiguration(job.ProcessingStepId);
        if (configResult.TryPickProblems(out var problems, out var config))
        {
            FailJob(job, job.Status as InProgress, $"Missing configuration for processing step '{job.ProcessingStepId}': {problems}");
            return;
        }

        var stepName = processingStepService.TryGet(job.ProcessingStepId).TryPickProblems(out _, out var step)
            ? "(unknown)"
            : step.Name;
        var logKey = LogS3Key(job.PipelineId, job.ArtifactBundleId, job.Id);

        await SubmitGuardedAsync(job, logKey, async token =>
        {
            var secretName = await GetSecretNameIfExists(job.PipelineId, token);
            var s3SecretName = await CreateS3CredentialsSecretAsync(job.Id, token);
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

            await SubmitOrReattachAsync(job, spec, stepName, s3SecretName, logKey, token);
        }, ct);
    }

    private async Task SubmitOrReattachAsync(Job job, KubernetesJobSpec spec, string stepName, string s3SecretName, string logKey, CancellationToken ct)
    {
        // The per-job S3 secret must outlive a cancelled watcher. A self-deploy step helm-upgrades this
        // very controller mid-run, which restarts the pod and cancels `ct` (ApplicationStopping) while the
        // K8s Job is still running. Deleting the secret then breaks the Job's still-pending s3-upload
        // container (CreateContainerConfigError) and the Job can never reach Succeeded — wedging it forever.
        // So clean up ONLY once the Job reaches a terminal state; on cancellation leave the secret intact so
        // the reattach on next startup finds working credentials.
        var reachedTerminal = false;
        try
        {
            var existing = await kubernetesClient.TryGetJobStatusAsync(options.Namespace, spec.Name, ct);

            if (existing is null)
            {
                logger.LogInformation("Creating K8s Job '{JobName}' for step '{StepName}' (pipeline '{PipelineId}')",
                    spec.Name, stepName, job.PipelineId);
                await kubernetesClient.CreateJobAsync(options.Namespace, spec, ct);
            }
            else
            {
                logger.LogInformation("Reattaching to existing K8s Job '{JobName}' for step '{StepName}' (pipeline '{PipelineId}', phase={Phase})",
                    spec.Name, stepName, job.PipelineId, existing.Phase);
            }

            var startedAt = EnsureInProgress(job);

            if (existing is { Phase: JobPhase.Succeeded })
            {
                await UploadLogsAsync(spec.Name, logKey, ct);
                WriteDone(job, startedAt);
                reachedTerminal = true;
                return;
            }

            if (existing is { Phase: JobPhase.Failed } failed)
            {
                await UploadLogsAsync(spec.Name, logKey, ct);
                FailJob(job, startedAt, failed.Message ?? "K8s job failed");
                reachedTerminal = true;
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);

                var status = await kubernetesClient.GetJobStatusAsync(options.Namespace, spec.Name, ct);

                switch (status.Phase)
                {
                    case JobPhase.Succeeded:
                        logger.LogInformation("K8s Job '{JobName}' for step '{StepName}' succeeded", spec.Name, stepName);
                        await UploadLogsAsync(spec.Name, logKey, ct);
                        WriteDone(job, startedAt);
                        reachedTerminal = true;
                        return;
                    case JobPhase.Failed:
                        logger.LogWarning("K8s Job '{JobName}' for step '{StepName}' failed: {Message}", spec.Name, stepName, status.Message);
                        await UploadLogsAsync(spec.Name, logKey, ct);
                        FailJob(job, startedAt, status.Message ?? "K8s job failed");
                        reachedTerminal = true;
                        return;
                }
            }

            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            if (reachedTerminal)
                await CleanupS3SecretAsync(s3SecretName);
        }
    }

    // Wraps the submission phase (per-job S3 secret creation + submit/reattach). Any failure here
    // happens while the job is still Scheduled — i.e. a K8s-API-unavailable stall. Rather than let it
    // bubble up as a silently-retried-forever idle (JobRunner would respawn the watcher every tick),
    // count the attempt, surface it to the job's run log, and after MaxSubmissionAttempts fail the job.
    private async Task SubmitGuardedAsync(Job job, string logKey, Func<CancellationToken, Task> submit, CancellationToken ct)
    {
        try
        {
            await submit(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown (e.g. a self-deploy helm-upgrade restarting this controller): leave the job
            // untouched so the next start reattaches or re-submits.
            throw;
        }
        catch (Exception ex)
        {
            // A failure once the job has reached InProgress is an in-flight/poll error, not a
            // submission stall — rethrow so the watcher respawns and reattaches to the running K8s
            // Job (idempotent). Only pre-InProgress failures are retried with the attempt budget.
            if (jobService.TryGetJob<Job>(job.Id, out var current) && current.Status is InProgress)
                throw;

            await HandleSubmissionFailureAsync(job, logKey, ex, ct);
        }
    }

    private async Task HandleSubmissionFailureAsync(Job job, string logKey, Exception ex, CancellationToken ct)
    {
        var attempt = ((job.Status as Scheduled)?.Attempts ?? 0) + 1;

        if (attempt >= MaxSubmissionAttempts)
        {
            var reason = $"K8s submission failed after {attempt} attempts: {ex.Message}";
            await AppendControllerLogAsync(logKey, $"controller: {reason} — giving up", ct);
            // Best-effort cleanup of the per-job S3 secret in case an earlier attempt created it
            // before the K8s Job submission failed. Deterministic name; safe if it never existed.
            await CleanupS3SecretAsync(S3SecretName(job.Id));
            FailJob(job, job.Status as InProgress, reason);
            logger.LogError(ex, "Giving up on job '{JobId}' (pipeline '{PipelineId}') after {Attempts} failed submission attempts",
                job.Id, job.PipelineId, attempt);
            return;
        }

        await AppendControllerLogAsync(logKey,
            $"controller: failed to submit K8s Job — {ex.Message}; retry {attempt}/{MaxSubmissionAttempts - 1}", ct);
        logger.LogWarning(ex, "Submission attempt {Attempt} of {Max} failed for job '{JobId}' (pipeline '{PipelineId}'); retrying",
            attempt, MaxSubmissionAttempts, job.Id, job.PipelineId);

        // Persist the incremented attempt so the next watcher (after respawn) resumes the count and
        // the stuck-and-retrying state survives a restart.
        LogIfFailed(jobService.UpdateJob<Job>(job.Id, j =>
            j.Status is Scheduled s ? j with { Status = s with { Attempts = attempt } } : j), job.Id);

        // Hold the watcher slot briefly so retries ride out a transient outage instead of hammering
        // K8s at the JobRunner tick rate. The watcher then exits and JobRunner respawns it.
        try
        {
            await Task.Delay(SubmissionRetryDelay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — the persisted attempt count is enough; retry on next start.
        }
    }

    // Appends a single controller-side line to the job's S3 run log (read-modify-write). Used to
    // explain a submission stall in the same place the operator reads pod logs — no K8s Job/pod
    // exists yet, so there is nothing else to show there. Safe: one watcher per job, attempts serial.
    private async Task AppendControllerLogAsync(string logKey, string line, CancellationToken ct)
    {
        try
        {
            var existing = string.Empty;
            try
            {
                using var response = await s3.GetObjectAsync(storageOptions.Bucket, logKey, ct);
                using var reader = new StreamReader(response.ResponseStream);
                existing = await reader.ReadToEndAsync(ct);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // First controller line for this job — no log object exists yet.
            }

            var bytes = Encoding.UTF8.GetBytes(existing + line + "\n");
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = logKey,
                InputStream = new MemoryStream(bytes),
                ContentType = "text/plain; charset=utf-8",
            }, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to append controller log line to '{LogKey}'", logKey);
        }
    }

    private DateTimeOffset EnsureInProgress(Job job)
    {
        if (job.Status is InProgress inProgress) return inProgress.StartedAt;

        var startedAt = time.GetUtcNow();
        LogIfFailed(jobService.UpdateJob<Job>(job.Id, j => j with { Status = new InProgress(startedAt) }), job.Id);
        return startedAt;
    }

    private void WriteDone(Job job, DateTimeOffset startedAt)
    {
        var completedAt = time.GetUtcNow();
        LogIfFailed(jobService.UpdateJob<Job>(job.Id, j => j with
        {
            Status = new Done(startedAt, completedAt),
        }), job.Id);

        logger.LogInformation("Job '{JobId}' for pipeline '{PipelineId}' completed in {ElapsedMs}ms",
            job.Id, job.PipelineId, (long)(completedAt - startedAt).TotalMilliseconds);
    }

    private void FailJob(Job job, InProgress? currentInProgress, string reason)
    {
        var startedAt = currentInProgress?.StartedAt ?? time.GetUtcNow();
        FailJob(job, startedAt, reason);
    }

    private void FailJob(Job job, DateTimeOffset startedAt, string reason)
    {
        var failedAt = time.GetUtcNow();
        LogIfFailed(jobService.UpdateJob<Job>(job.Id, j => j with
        {
            Status = new Failed(startedAt, failedAt, reason),
        }), job.Id);

        logger.LogWarning("Job '{JobId}' for pipeline '{PipelineId}' failed after {ElapsedMs}ms: {Reason}",
            job.Id, job.PipelineId, (long)(failedAt - startedAt).TotalMilliseconds, reason);
    }

    private void LogIfFailed(Result result, Id<Job> jobId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of job '{JobId}' (it may have been deleted)", jobId);
    }

    private async Task<string> CreateS3CredentialsSecretAsync(Id<Job> jobId, CancellationToken ct)
    {
        var creds = await s3CredentialsProvider.GetCredentialsAsync(ct);
        var secretName = S3SecretName(jobId);

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

    // Deterministic per-job S3 credentials secret name — reconstructable for cleanup without threading
    // it through the failure path. Matches the name used at creation.
    private static string S3SecretName(Id<Job> jobId) => $"olve-s3-{jobId.Value.Value:N}";

    internal static string S3Prefix(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId)
        => $"bundles/{pipelineId.Value.Value:N}/{artifactBundleId.Value.Value:N}";

    internal static string LogS3Key(Id<Pipeline> pipelineId, Id<ArtifactBundle> artifactBundleId, Id<Job> jobId)
        => $"{S3Prefix(pipelineId, artifactBundleId)}/logs/{jobId.Value.Value:N}.log";

    // Determinism is load-bearing: on app restart, the watcher dispatcher derives this name from the
    // persisted Id<Job> and calls TryGetJobStatusAsync to decide whether to submit or reattach.
    private static string JobName(Id<Job> jobId)
    {
        var name = $"olve-{jobId.Value.Value:N}";
        return name.Length > 63 ? name[..63] : name;
    }
}
