using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

public class KubernetesJobExecutor(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    IKubernetesClient kubernetesClient,
    KubernetesOptions options,
    IPipelineSecretStore secretStore,
    ICredentialsProvider<S3Credentials> s3CredentialsProvider,
    IAmazonS3 s3,
    StorageOptions storageOptions,
    AttemptService attempts,
    ILogger<KubernetesJobExecutor> logger)
    : JobExecutorBase(scopeFactory, registry, lifetime, logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // A K8s-submission failure keeps the attempt Scheduled and is retried by JobRunner respawning the
    // watcher. Cap the submissions so a genuine outage fails the attempt loudly instead of looping forever.
    internal const int MaxSubmissionAttempts = 3; // initial attempt + 2 retries

    // Held between retries so they ride out a transient outage rather than hammering K8s at the
    // JobRunner tick rate. Overridable so tests don't wait real seconds.
    internal TimeSpan SubmissionRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    // TODO(B5): replace with the harness spec — harness-base init container fetching step.tar.gz +
    // input.json via presigned GETs, the pinned image from the version's manifest, per-step
    // secretKeyRefs, activeDeadlineSeconds. Until then a step pod only proves the plumbing and fails.
    internal const string PlaceholderScript =
        "echo \"olve: step '$OLVE_STEP' (run #$OLVE_RUN, attempt $OLVE_ATTEMPT) — harness executor not implemented yet (B5)\" >&2; exit 1";

    protected internal override async Task RunToCompletionAsync(Attempt attempt, CancellationToken ct)
    {
        var logKey = StorageLayout.AttemptLogKey(attempt);

        await SubmitGuardedAsync(attempt, logKey, async token =>
        {
            var secretName = await GetSecretNameIfExists(attempt.PipelineId, token);
            var s3SecretName = await CreateS3CredentialsSecretAsync(attempt.Id, token);

            var spec = new KubernetesJobSpec(
                Name: attempt.KubernetesJobName,
                Image: options.DefaultImage,
                Script: PlaceholderScript,
                OutputBundleS3Prefix: StorageLayout.AttemptFilesPrefix(attempt),
                S3HelperImage: options.S3HelperImage,
                S3Bucket: options.S3Bucket,
                S3Endpoint: options.S3Endpoint,
                S3CredentialsSecretName: s3SecretName,
                S3SkipCertValidation: options.S3SkipCertValidation,
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["OLVE_STEP"] = attempt.StepName,
                    ["OLVE_RUN"] = attempt.RunNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["OLVE_ATTEMPT"] = attempt.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                SecretName: secretName,
                RuntimeClassName: options.RuntimeClassName);

            await SubmitOrReattachAsync(attempt, spec, s3SecretName, logKey, token);
        }, ct);
    }

    private async Task SubmitOrReattachAsync(Attempt attempt, KubernetesJobSpec spec, string s3SecretName, string logKey, CancellationToken ct)
    {
        // The per-attempt S3 secret must outlive a cancelled watcher. A self-deploy step helm-upgrades this
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
                logger.LogInformation("Creating K8s Job '{JobName}' for step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}')",
                    spec.Name, attempt.StepName, attempt.RunNumber, attempt.Number, attempt.PipelineId);
                await kubernetesClient.CreateJobAsync(options.Namespace, spec, ct);
            }
            else
            {
                logger.LogInformation("Reattaching to existing K8s Job '{JobName}' for step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}', phase={Phase})",
                    spec.Name, attempt.StepName, attempt.RunNumber, attempt.Number, attempt.PipelineId, existing.Phase);
            }

            LogIfFailed(attempts.MarkInProgress(attempt.Id), attempt.Id);

            if (existing is { Phase: JobPhase.Succeeded })
            {
                await UploadLogsAsync(spec.Name, logKey, ct);
                Succeed(attempt);
                reachedTerminal = true;
                return;
            }

            if (existing is { Phase: JobPhase.Failed } failed)
            {
                await UploadLogsAsync(spec.Name, logKey, ct);
                Fail(attempt, failed.Message ?? "K8s job failed");
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
                        logger.LogInformation("K8s Job '{JobName}' for step '{StepName}' succeeded", spec.Name, attempt.StepName);
                        await UploadLogsAsync(spec.Name, logKey, ct);
                        Succeed(attempt);
                        reachedTerminal = true;
                        return;
                    case JobPhase.Failed:
                        logger.LogWarning("K8s Job '{JobName}' for step '{StepName}' failed: {Message}", spec.Name, attempt.StepName, status.Message);
                        await UploadLogsAsync(spec.Name, logKey, ct);
                        Fail(attempt, status.Message ?? "K8s job failed");
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

    // Wraps the submission phase (per-attempt S3 secret creation + submit/reattach). Any failure here
    // happens while the attempt is still Scheduled — i.e. a K8s-API-unavailable stall. Rather than let it
    // bubble up as a silently-retried-forever idle (JobRunner would respawn the watcher every tick),
    // count the submission, surface it to the attempt's log, and after MaxSubmissionAttempts fail it.
    private async Task SubmitGuardedAsync(Attempt attempt, string logKey, Func<CancellationToken, Task> submit, CancellationToken ct)
    {
        try
        {
            await submit(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown (e.g. a self-deploy helm-upgrade restarting this controller): leave the attempt
            // untouched so the next start reattaches or re-submits.
            throw;
        }
        catch (Exception ex)
        {
            // A failure once the attempt has reached InProgress is an in-flight/poll error, not a
            // submission stall — rethrow so the watcher respawns and reattaches to the running K8s
            // Job (idempotent). Only pre-InProgress failures are retried with the submission budget.
            if (attempts.TryGet(attempt.Id, out var current) && current.Status is InProgress)
                throw;

            await HandleSubmissionFailureAsync(attempt, logKey, ex, ct);
        }
    }

    private async Task HandleSubmissionFailureAsync(Attempt attempt, string logKey, Exception ex, CancellationToken ct)
    {
        var submission = ((attempt.Status as Scheduled)?.SubmissionAttempts ?? 0) + 1;

        if (submission >= MaxSubmissionAttempts)
        {
            var reason = $"K8s submission failed after {submission} attempts: {ex.Message}";
            await AppendControllerLogAsync(logKey, $"controller: {reason} — giving up", ct);
            // Best-effort cleanup of the per-attempt S3 secret in case an earlier try created it
            // before the K8s Job submission failed. Deterministic name; safe if it never existed.
            await CleanupS3SecretAsync(S3SecretName(attempt.Id));
            Fail(attempt, reason);
            logger.LogError(ex, "Giving up on attempt '{AttemptId}' of step '{StepName}' (pipeline '{PipelineId}') after {Submissions} failed submissions",
                attempt.Id, attempt.StepName, attempt.PipelineId, submission);
            return;
        }

        await AppendControllerLogAsync(logKey,
            $"controller: failed to submit K8s Job — {ex.Message}; retry {submission}/{MaxSubmissionAttempts - 1}", ct);
        logger.LogWarning(ex, "Submission {Submission} of {Max} failed for attempt '{AttemptId}' of step '{StepName}' (pipeline '{PipelineId}'); retrying",
            submission, MaxSubmissionAttempts, attempt.Id, attempt.StepName, attempt.PipelineId);

        // Persist the incremented count so the next watcher (after respawn) resumes it and the
        // stuck-and-retrying state survives a restart.
        LogIfFailed(attempts.RecordSubmissionFailure(attempt.Id), attempt.Id);

        // Hold the watcher slot briefly so retries ride out a transient outage instead of hammering
        // K8s at the JobRunner tick rate. The watcher then exits and JobRunner respawns it.
        try
        {
            await Task.Delay(SubmissionRetryDelay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — the persisted count is enough; retry on next start.
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

    // TODO(B5): collect output.json (envelope: succeeded/skipped) and pass the output along.
    private void Succeed(Attempt attempt)
    {
        LogIfFailed(attempts.MarkSucceeded(attempt.Id), attempt.Id);
        LogFinished(attempt.Id);
    }

    private void Fail(Attempt attempt, string reason)
    {
        LogIfFailed(attempts.MarkFailed(attempt.Id, reason), attempt.Id);
        LogFinished(attempt.Id);
    }

    private void LogFinished(Id<Attempt> attemptId)
    {
        if (!attempts.TryGet(attemptId, out var after))
            return;

        var elapsed = after.FinishedAt() - after.StartedAt();
        logger.LogInformation("Attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') finished {Status} in {ElapsedMs}ms",
            after.Number, after.StepName, after.RunNumber, after.PipelineId, after.Status.Discriminator(), (long?)elapsed?.TotalMilliseconds);
    }

    private void LogIfFailed(Result result, Id<Attempt> attemptId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of attempt '{AttemptId}' (it may have been deleted)", attemptId);
    }

    private void LogIfFailed<T>(Result<T> result, Id<Attempt> attemptId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of attempt '{AttemptId}' (it may have been deleted)", attemptId);
    }

    private async Task<string> CreateS3CredentialsSecretAsync(Id<Attempt> attemptId, CancellationToken ct)
    {
        var creds = await s3CredentialsProvider.GetCredentialsAsync(ct);
        var secretName = S3SecretName(attemptId);

        // Consumed as env by the s3-download/s3-upload helpers (S3SyncScript).
        var data = new Dictionary<string, string>
        {
            ["S3_ACCESS_KEY"] = creds.AccessKey,
            ["S3_SECRET_KEY"] = creds.SecretKey,
        };
        if (creds.SessionToken is not null)
            data["S3_SESSION_TOKEN"] = creds.SessionToken;

        await kubernetesClient.CreateSecretAsync(options.Namespace, secretName, data, ct);
        logger.LogInformation("Created S3 credentials secret '{SecretName}'", secretName);

        return secretName;
    }

    private async Task UploadLogsAsync(string k8sJobName, string logKey, CancellationToken ct)
    {
        try
        {
            string? logs;
            try
            {
                logs = await kubernetesClient.GetPodLogsAsync(options.Namespace, k8sJobName, container: "runner", ct: ct);
            }
            catch (HttpRequestException)
            {
                // The runner never started (e.g. s3-download failed first) — the helper logs below say why.
                logs = "";
            }

            if (logs is null)
            {
                logger.LogWarning("No pod found for K8s Job '{JobName}' — cannot persist logs", k8sJobName);
                return;
            }

            // The bundle helpers are silent on success; when one fails (e.g. a 403 moving the bundle)
            // its output is the only explanation, and the pod is deleted by TTL — so keep it too.
            foreach (var helper in (string[])["s3-download", "s3-upload"])
            {
                var helperLogs = await TryGetContainerLogsAsync(k8sJobName, helper, ct);
                if (!string.IsNullOrWhiteSpace(helperLogs))
                    logs += $"\n--- {helper} ---\n{helperLogs}";
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

    // Absent containers (no s3-download on production jobs) and never-started ones return no logs.
    private async Task<string?> TryGetContainerLogsAsync(string k8sJobName, string container, CancellationToken ct)
    {
        try
        {
            return await kubernetesClient.GetPodLogsAsync(options.Namespace, k8sJobName, container, ct);
        }
        catch (HttpRequestException)
        {
            return null;
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

    // Deterministic per-attempt S3 credentials secret name — reconstructable for cleanup without
    // threading it through the failure path. Matches the name used at creation.
    private static string S3SecretName(Id<Attempt> attemptId) => $"olve-s3-{attemptId.Value.Value:N}";
}
