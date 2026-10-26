using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// Runs one attempt as a harness step Job (spec §5.1, harness/README.md "Pod template"):
/// <list type="number">
/// <item><b>Submit</b> (only when no Job with the attempt's deterministic name exists): upload the
/// assembled <c>input.json</c>, pre-sign the URLs, upsert the per-attempt URL Secret, create the Job.
/// An existing Job — after a controller restart — is reattached; neither is re-created.</item>
/// <item><b>Watch</b>: poll the pod until it reaches a verdict. A container stuck in a fatal waiting
/// state (ErrImagePull, CreateContainerConfigError, …) fails the attempt after a short grace and the
/// Job is deleted. An attempt cancelled while InProgress (drain with cancel-running, API cancel) has
/// its Job deleted.</item>
/// <item><b>Finish</b>: persist the logs (<c>step</c>, plus <c>olve-init</c> when the step never ran),
/// collect <c>result.tar.gz</c> into the attempt's storage layout, resolve the outcome and mark the
/// attempt; then delete the archive and the URL Secret. On controller shutdown (cancellation) nothing
/// is cleaned up, so the reattach finds working URLs.</item>
/// </list>
/// </summary>
public class KubernetesJobExecutor(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    HarnessJobControl control,
    HarnessJobFactory factory,
    IHarnessStorage storage,
    AttemptService attempts,
    AttemptLaunchService launches,
    CallbackInputUrls callbacks,
    ILogger<KubernetesJobExecutor> logger)
    : JobExecutorBase(scopeFactory, registry, lifetime, logger)
{
    // A K8s-submission failure keeps the attempt Scheduled and is retried by JobRunner respawning the
    // watcher. Cap the submissions so a genuine outage fails the attempt loudly instead of looping forever.
    internal const int MaxSubmissionAttempts = 3; // initial attempt + 2 retries

    // Held between retries so they ride out a transient outage rather than hammering K8s at the
    // JobRunner tick rate. Overridable so tests don't wait real seconds.
    internal TimeSpan SubmissionRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    protected internal override async Task RunToCompletionAsync(Attempt attempt, CancellationToken ct)
    {
        var logKey = StorageLayout.AttemptLogKey(attempt);

        await SubmitGuardedAsync(attempt, logKey, async token =>
        {
            var existing = await control.GetStateAsync(attempt.KubernetesJobName, token);

            if (existing is null)
            {
                if (attempt.Status is InProgress)
                {
                    // Never re-run an attempt that already started: its Job is gone (deleted by hand, or
                    // collected by TTL during a long outage), so its outcome is unknown.
                    Fail(attempt, $"K8s Job '{attempt.KubernetesJobName}' no longer exists; the attempt's outcome is unknown");
                    await control.DeleteSecretAsync(HarnessStorageKeys.UrlSecretName(attempt.Id));
                    return;
                }

                if (!await SubmitAsync(attempt, token))
                    return;
            }
            else
            {
                logger.LogInformation("Reattaching to K8s Job '{JobName}' of step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}')",
                    attempt.KubernetesJobName, attempt.StepName, attempt.RunNumber, attempt.Number, attempt.PipelineId);
            }

            LogIfFailed(attempts.MarkInProgress(attempt.Id), attempt.Id);
            await WatchAsync(attempt, logKey, token);
        }, ct);
    }

    /// <summary>
    /// Uploads the input and creates the URL Secret + Job. False when the attempt was failed instead, or was
    /// cancelled or made obsolete meanwhile (then no Job is created).
    /// </summary>
    private async Task<bool> SubmitAsync(Attempt attempt, CancellationToken ct)
    {
        if (IsAbandoned(attempt.Id))
            return false;

        if (launches.Get(attempt.Id).TryPickProblems(out var problems, out var launch))
        {
            Fail(attempt, "cannot launch: " + string.Join("; ", problems.Select(p => p.FormattedMessage)));
            return false;
        }

        await storage.PutTextAsync(HarnessStorageKeys.AttemptInputKey(attempt), launch.InputJson, "application/json", ct);

        var callbackUrls = launch.Plan is { } plan ? await callbacks.ForRunAsync(launch.Run.Id, plan, ct) : null;
        Result<HarnessJobSpec> specResult;
        using (var input = JsonDocument.Parse(launch.InputJson))
        {
            specResult = await factory.CreateStepJobAsync(
                attempt, launch.Version.BinaryKey, launch.Run.VersionStamp, launch.Step, input.RootElement, launch.Cases, callbackUrls, ct);
        }

        if (specResult.TryPickProblems(out var specProblems, out var spec))
        {
            Fail(attempt, "cannot build the step Job: " + string.Join("; ", specProblems.Select(p => p.FormattedMessage)));
            return false;
        }

        // The upload and the Secret took time: a newer attempt of the step may have made this one obsolete.
        if (IsAbandoned(attempt.Id))
        {
            logger.LogInformation("Not creating K8s Job '{JobName}': attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') was cancelled or made obsolete while it was being submitted",
                spec.Job.Metadata.Name, attempt.Number, attempt.StepName, attempt.RunNumber, attempt.PipelineId);
            return false;
        }

        var created = await control.SubmitAsync(spec, ct);
        logger.LogInformation("{Action} K8s Job '{JobName}' for step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, version {VersionNumber}, image {Image}, pipeline '{PipelineId}')",
            created ? "Created" : "Found existing", spec.Job.Metadata.Name, attempt.StepName, attempt.RunNumber, attempt.Number,
            launch.Version.Number, launch.Step.Image, attempt.PipelineId);
        return true;
    }

    private async Task WatchAsync(Attempt attempt, string logKey, CancellationToken ct)
    {
        var jobName = attempt.KubernetesJobName;
        var urlSecret = HarnessStorageKeys.UrlSecretName(attempt.Id);

        var wait = await control.WaitAsync(jobName, PollInterval, () => IsAbandoned(attempt.Id), timeout: null, ct);

        switch (wait)
        {
            case HarnessWait.Aborted:
            {
                logger.LogInformation("Attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') was cancelled or made obsolete; deleting K8s Job '{JobName}'",
                    attempt.Number, attempt.StepName, attempt.RunNumber, attempt.PipelineId, jobName);
                var logs = await control.GetLogsAsync(jobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
                await PutLogAsync(logKey, logs.Combined(HarnessJobBuilder.StepContainerName) + "controller: attempt cancelled or obsolete; K8s Job deleted\n", ct);
                await control.DeleteJobAsync(jobName);
                await control.DeleteSecretAsync(urlSecret);
                await storage.DeleteAsync(HarnessStorageKeys.AttemptResultArchiveKey(attempt), ct);
                return;
            }

            case HarnessWait.Missing:
                if (!IsAbandoned(attempt.Id))
                    Fail(attempt, $"K8s Job '{jobName}' disappeared while the attempt was running");
                await control.DeleteSecretAsync(urlSecret);
                return;

            case HarnessWait.Finished finished:
                await FinishAsync(attempt, logKey, finished, ct);
                await storage.DeleteAsync(HarnessStorageKeys.AttemptResultArchiveKey(attempt), ct);
                await control.DeleteSecretAsync(urlSecret);
                return;

            default:
                throw new InvalidOperationException($"Unexpected wait outcome '{wait.GetType().Name}'.");
        }
    }

    private async Task FinishAsync(Attempt attempt, string logKey, HarnessWait.Finished finished, CancellationToken ct)
    {
        var logs = await control.GetLogsAsync(attempt.KubernetesJobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
        var logText = logs.Combined(HarnessJobBuilder.StepContainerName);
        if (finished.DeletedStuck)
            logText += $"controller: {HarnessExitCodes.Classify(finished.State)?.Message}; K8s Job deleted\n";
        await PutLogAsync(logKey, logText, ct);

        var state = finished.State;
        LogIfFailed(attempts.Update(attempt.Id, a => a with
        {
            Timings = new AttemptTimings(state.PodCreatedAt, state.Init?.StartedAt, state.Init?.FinishedAt, state.Main?.StartedAt, state.Main?.FinishedAt,
                finished.ObservedAt),
        }), attempt.Id);

        var collection = await storage.CollectAsync(attempt, ct);
        var outcome = HarnessOutcome.Resolve(finished.State, collection.Archive, collection.FileKeys,
            HarnessSentinels.TryExtractResult(logs.Main));

        switch (outcome)
        {
            case HarnessAttemptResult.Succeeded s:
                if (attempts.MarkSucceeded(attempt.Id, s.OutputJson).TryPickProblems(out var problems))
                    Fail(attempt, "output rejected: " + string.Join("; ", problems.Select(p => p.FormattedMessage)));
                break;
            case HarnessAttemptResult.Skipped s:
                LogIfFailed(attempts.MarkSkipped(attempt.Id, s.Reason), attempt.Id);
                break;
            case HarnessAttemptResult.Failed f:
                Fail(attempt, f.Message);
                break;
        }

        LogFinished(attempt.Id);
    }

    // Obsolete: latest-wins replaced the attempt while its Job was being submitted (MarkInProgress then failed).
    private bool IsAbandoned(Id<Attempt> id) => attempts.TryGet(id, out var current) && current.Status is Cancelled or Obsolete;

    // Wraps the submission phase (input upload, URL Secret, submit/reattach). A failure while the attempt
    // is still Scheduled is a K8s/storage-unavailable stall: count it, surface it in the attempt's log, and
    // fail after MaxSubmissionAttempts instead of retrying forever.
    private async Task SubmitGuardedAsync(Attempt attempt, string logKey, Func<CancellationToken, Task> submit, CancellationToken ct)
    {
        try
        {
            await submit(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown (e.g. a self-deploy helm-upgrading this controller): leave the attempt untouched so
            // the next start reattaches or re-submits.
            throw;
        }
        catch (Exception ex)
        {
            // Once InProgress, a failure is an in-flight/poll error, not a submission stall — rethrow so the
            // watcher respawns and reattaches to the running Job (idempotent).
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
            await control.DeleteSecretAsync(HarnessStorageKeys.UrlSecretName(attempt.Id));
            Fail(attempt, reason);
            logger.LogError(ex, "Giving up on attempt '{AttemptId}' of step '{StepName}' (pipeline '{PipelineId}') after {Submissions} failed submissions",
                attempt.Id, attempt.StepName, attempt.PipelineId, submission);
            return;
        }

        await AppendControllerLogAsync(logKey,
            $"controller: failed to submit K8s Job — {ex.Message}; retry {submission}/{MaxSubmissionAttempts - 1}", ct);
        logger.LogWarning(ex, "Submission {Submission} of {Max} failed for attempt '{AttemptId}' of step '{StepName}' (pipeline '{PipelineId}'); retrying",
            submission, MaxSubmissionAttempts, attempt.Id, attempt.StepName, attempt.PipelineId);

        LogIfFailed(attempts.RecordSubmissionFailure(attempt.Id), attempt.Id);

        try
        {
            await Task.Delay(SubmissionRetryDelay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — the persisted count is enough; retry on next start.
        }
    }

    private async Task AppendControllerLogAsync(string logKey, string line, CancellationToken ct)
    {
        try
        {
            var existing = await storage.GetTextAsync(logKey, ct) ?? "";
            await storage.PutTextAsync(logKey, existing + line + "\n", "text/plain; charset=utf-8", ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to append controller log line to '{LogKey}'", logKey);
        }
    }

    private async Task PutLogAsync(string logKey, string text, CancellationToken ct)
    {
        try
        {
            await storage.PutTextAsync(logKey, text, "text/plain; charset=utf-8", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Failed to persist logs to '{LogKey}'", logKey);
        }
    }

    private void Fail(Attempt attempt, string reason)
    {
        LogIfFailed(attempts.MarkFailed(attempt.Id, reason), attempt.Id);
        logger.LogWarning("Attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') failed: {Reason}",
            attempt.Number, attempt.StepName, attempt.RunNumber, attempt.PipelineId, reason);
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
}
