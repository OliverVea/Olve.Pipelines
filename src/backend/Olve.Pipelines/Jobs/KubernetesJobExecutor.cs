using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Shared.Persistence;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// Runs one attempt as a harness step Job (src/harness/README.md "Pod template"):
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
/// attempt; once that is saved delete the archive, and delete the URL Secret. On controller shutdown (cancellation) nothing
/// is cleaned up, so the reattach finds working URLs.</item>
/// </list>
/// An attempt in preflight runs the same way as a Job of its own (<c>preflight &lt;step&gt;</c>, no result upload): its
/// envelope comes from the log, a success makes the attempt wait (<see cref="AttemptService.FinishPreflight"/>) and a
/// failure fails it. Its log starts the attempt's log.
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
    IStateDurability durability,
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

    protected internal override Task RunToCompletionAsync(Attempt attempt, CancellationToken ct) =>
        RunAsync(new CdHarnessAttempt(attempt, attempts, launches, callbacks), ct);

    /// <summary>Drives one attempt of any kind from submission (or reattach) to its verdict.</summary>
    internal async Task RunAsync(IHarnessAttempt attempt, CancellationToken ct)
    {
        var id = attempt.Identity;

        await SubmitGuardedAsync(attempt, async token =>
        {
            var existing = await control.GetStateAsync(id.JobName, token);

            if (existing is null)
            {
                if (attempt.IsInProgress())
                {
                    // Never re-run an attempt that already started: its Job is gone (deleted by hand, or
                    // collected by TTL during a long outage), so its outcome is unknown.
                    Fail(attempt, $"K8s Job '{id.JobName}' no longer exists; the attempt's outcome is unknown", AttemptFailureKind.JobLost);
                    await control.DeleteSecretAsync(id.UrlSecretName);
                    return;
                }

                if (!await SubmitAsync(attempt, token))
                    return;
            }
            else
            {
                logger.LogInformation("Reattaching to K8s Job '{JobName}' of step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}')",
                    id.JobName, id.StepName, id.RunNumber, id.AttemptNumber, id.PipelineId);
            }

            LogIfFailed(attempt.MarkInProgress(), id);
            await WatchAsync(attempt, token);
        }, ct);
    }

    /// <summary>
    /// Uploads the input and creates the URL Secret + Job. False when the attempt was failed instead, or was
    /// cancelled or made obsolete meanwhile (then no Job is created).
    /// </summary>
    private async Task<bool> SubmitAsync(IHarnessAttempt attempt, CancellationToken ct)
    {
        var id = attempt.Identity;
        if (attempt.IsAbandoned())
            return false;

        if (attempt.Launch().TryPickProblems(out var problems, out var launch))
        {
            Fail(attempt, "cannot launch: " + string.Join("; ", problems.Select(p => p.FormattedMessage)), AttemptFailureKind.LaunchFailed);
            return false;
        }

        await storage.PutTextAsync(id.InputKey, launch.InputJson, "application/json", ct);

        var callbackUrls = await attempt.CallbackUrlsAsync(ct);
        Result<HarnessJobSpec> specResult;
        using (var input = JsonDocument.Parse(launch.InputJson))
        {
            specResult = await factory.CreateStepJobAsync(
                id, launch.BinaryKey, launch.VersionStamp, launch.Step, input.RootElement, launch.Cases, callbackUrls, launch.Cell, ct);
        }

        if (specResult.TryPickProblems(out var specProblems, out var spec))
        {
            Fail(attempt, "cannot build the step Job: " + string.Join("; ", specProblems.Select(p => p.FormattedMessage)), AttemptFailureKind.LaunchFailed);
            return false;
        }

        // A Job no saved attempt owns would be an orphan after a crash.
        if ((await durability.SavedAsync(ct)).TryPickProblems(out var unsaved))
            throw new IOException(unsaved.Single().FormattedMessage);

        // The upload, the Secret and the save took time: a newer attempt of the step may have made this one obsolete.
        if (attempt.IsAbandoned())
        {
            logger.LogInformation("Not creating K8s Job '{JobName}': attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') was cancelled or made obsolete while it was being submitted",
                spec.Job.Metadata.Name, id.AttemptNumber, id.StepName, id.RunNumber, id.PipelineId);
            return false;
        }

        var created = await control.SubmitAsync(spec, ct);
        logger.LogInformation("{Action} K8s Job '{JobName}' for step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, version {VersionNumber}, image {Image}, pipeline '{PipelineId}')",
            created ? "Created" : "Found existing", spec.Job.Metadata.Name, id.StepName, id.RunNumber, id.AttemptNumber,
            launch.VersionNumber, launch.Step.Image, id.PipelineId);
        return true;
    }

    private async Task WatchAsync(IHarnessAttempt attempt, CancellationToken ct)
    {
        var id = attempt.Identity;
        var jobName = id.JobName;
        var urlSecret = id.UrlSecretName;

        var wait = await control.WaitAsync(jobName, PollInterval, attempt.IsAbandoned, timeout: null, ct);

        switch (wait)
        {
            case HarnessWait.Aborted:
                {
                    logger.LogInformation("Attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') was cancelled or made obsolete; deleting K8s Job '{JobName}'",
                        id.AttemptNumber, id.StepName, id.RunNumber, id.PipelineId, jobName);
                    var logs = await control.GetLogsAsync(jobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
                    // A deleted attempt's run is gone: a log now would recreate its objects.
                    if (attempt.Finished() is not null)
                        await PutLogAsync(id.LogKey, logs.Combined(HarnessJobBuilder.StepContainerName) + "controller: attempt cancelled or obsolete; K8s Job deleted\n", ct);
                    await control.DeleteJobAsync(jobName);
                    await control.DeleteSecretAsync(urlSecret);
                    await storage.DeleteAsync(id.ResultArchiveKey, ct);
                    return;
                }

            case HarnessWait.Missing:
                if (!attempt.IsAbandoned())
                    Fail(attempt, $"K8s Job '{jobName}' disappeared while the attempt was running", AttemptFailureKind.JobLost);
                await control.DeleteSecretAsync(urlSecret);
                return;

            case HarnessWait.Finished finished when id.InPreflight:
                await FinishPreflightAsync(attempt, finished, ct);
                await control.DeleteSecretAsync(urlSecret);
                return;

            case HarnessWait.Finished finished:
                await FinishAsync(attempt, finished, ct);
                // Until the verdict is saved, a restarted server has only the archive to reach it again.
                if ((await durability.SavedAsync(ct)).TryPickProblems(out var unsaved))
                    logger.LogProblems(LogLevel.Warning, unsaved, "Keeping the result archive of attempt '{JobName}': its status is not saved", jobName);
                else
                    await storage.DeleteAsync(id.ResultArchiveKey, ct);
                await control.DeleteSecretAsync(urlSecret);
                return;

            default:
                throw new InvalidOperationException($"Unexpected wait outcome '{wait.GetType().Name}'.");
        }
    }

    private async Task FinishAsync(IHarnessAttempt attempt, HarnessWait.Finished finished, CancellationToken ct)
    {
        var id = attempt.Identity;
        var logs = await control.GetLogsAsync(id.JobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
        var logText = logs.Combined(HarnessJobBuilder.StepContainerName);
        if (finished.DeletedStuck)
            logText += $"controller: {HarnessExitCodes.Classify(finished.State)?.Message}; K8s Job deleted\n";
        if (attempt.PassedPreflight)
            logText = await PreflightLogAsync(id.LogKey, ct) + logText;
        await PutLogAsync(id.LogKey, logText, ct);

        var state = finished.State;
        LogIfFailed(attempt.RecordTimings(new AttemptTimings(state.PodCreatedAt, state.Init?.StartedAt, state.Init?.FinishedAt,
            state.Main?.StartedAt, state.Main?.FinishedAt, finished.ObservedAt)), id);

        var collection = await storage.CollectAsync(id, ct);
        var outcome = HarnessOutcome.Resolve(finished.State, collection.Archive, collection.FileKeys,
            HarnessSentinels.TryExtractResult(logs.Main));

        if (outcome.Links is { } links)
            LogIfFailed(attempt.AddLinks(links), id);

        switch (outcome)
        {
            case HarnessAttemptResult.Succeeded s:
                if (attempt.MarkSucceeded(s.OutputJson).TryPickProblems(out var problems))
                    Fail(attempt, "output rejected: " + string.Join("; ", problems.Select(p => p.FormattedMessage)), AttemptFailureKind.Output);
                break;
            case HarnessAttemptResult.Skipped s:
                LogIfFailed(attempt.MarkSkipped(s.Reason), id);
                break;
            case HarnessAttemptResult.Failed f:
                Fail(attempt, f.Message, f.Kind);
                break;
        }

        LogFinished(attempt);
    }

    private async Task FinishPreflightAsync(IHarnessAttempt attempt, HarnessWait.Finished finished, CancellationToken ct)
    {
        var id = attempt.Identity;
        var logs = await control.GetLogsAsync(id.JobName, HarnessJobBuilder.StepContainerName, hasInit: true, ct);
        var logText = logs.Combined(HarnessJobBuilder.StepContainerName);
        if (finished.DeletedStuck)
            logText += $"controller: {HarnessExitCodes.Classify(finished.State)?.Message}; K8s Job deleted\n";
        await PutLogAsync(id.LogKey, logText, ct);

        switch (HarnessOutcome.ResolvePreflight(finished.State, HarnessSentinels.TryExtractResult(logs.Main)))
        {
            case HarnessAttemptResult.Failed f:
                Fail(attempt, f.Message, f.Kind);
                break;
            case var passed:
                if (attempt.Launch().TryPickProblems(out var problems, out var launch))
                {
                    Fail(attempt, "cannot finish the preflight: " + string.Join("; ", problems.Select(p => p.FormattedMessage)), AttemptFailureKind.LaunchFailed);
                    break;
                }
                if (attempt.FinishPreflight(passed.Links, launch.Step.ExternalInputs.Keys.ToList()).TryPickProblems(out var finishProblems, out var status))
                {
                    LogIfFailed(Result.Failure(finishProblems), id);
                    break;
                }
                logger.LogInformation("Preflight of step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}') succeeded; the attempt is {Status}",
                    id.StepName, id.RunNumber, id.AttemptNumber, id.PipelineId, status);
                break;
        }
    }

    /// <summary>The preflight's stored log, which the attempt's log starts with ("" when there is none).</summary>
    private async Task<string> PreflightLogAsync(string logKey, CancellationToken ct)
    {
        try
        {
            return await storage.GetTextAsync(logKey, ct) ?? "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not read the preflight log at '{LogKey}'", logKey);
            return "";
        }
    }

    // Wraps the submission phase (input upload, URL Secret, submit/reattach). A failure while the attempt
    // is still Scheduled is a K8s/storage-unavailable stall: count it, surface it in the attempt's log, and
    // fail after MaxSubmissionAttempts instead of retrying forever.
    private async Task SubmitGuardedAsync(IHarnessAttempt attempt, Func<CancellationToken, Task> submit, CancellationToken ct)
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
            if (attempt.IsInProgress())
                throw;

            await HandleSubmissionFailureAsync(attempt, ex, ct);
        }
    }

    private async Task HandleSubmissionFailureAsync(IHarnessAttempt attempt, Exception ex, CancellationToken ct)
    {
        var id = attempt.Identity;
        var submission = attempt.SubmissionAttempts + 1;

        if (submission >= MaxSubmissionAttempts)
        {
            var reason = $"K8s submission failed after {submission} attempts: {ex.Message}";
            await AppendControllerLogAsync(id.LogKey, $"controller: {reason} — giving up", ct);
            await control.DeleteSecretAsync(id.UrlSecretName);
            Fail(attempt, reason, AttemptFailureKind.LaunchFailed);
            logger.LogError(ex, "Giving up on attempt {AttemptNumber} of step '{StepName}' (pipeline '{PipelineId}') after {Submissions} failed submissions",
                id.AttemptNumber, id.StepName, id.PipelineId, submission);
            return;
        }

        await AppendControllerLogAsync(id.LogKey,
            $"controller: failed to submit K8s Job — {ex.Message}; retry {submission}/{MaxSubmissionAttempts - 1}", ct);
        logger.LogWarning(ex, "Submission {Submission} of {Max} failed for attempt {AttemptNumber} of step '{StepName}' (pipeline '{PipelineId}'); retrying",
            submission, MaxSubmissionAttempts, id.AttemptNumber, id.StepName, id.PipelineId);

        LogIfFailed(attempt.RecordSubmissionFailure(), id);

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

    private void Fail(IHarnessAttempt attempt, string reason, AttemptFailureKind kind)
    {
        var id = attempt.Identity;
        LogIfFailed(attempt.MarkFailed(reason, kind), id);
        logger.LogWarning("Attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') failed: {Reason}",
            id.AttemptNumber, id.StepName, id.RunNumber, id.PipelineId, reason);
    }

    private void LogFinished(IHarnessAttempt attempt)
    {
        if (attempt.Finished() is not { } finished)
            return;

        var id = attempt.Identity;
        logger.LogInformation("Attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}') finished {Status} in {ElapsedMs}ms",
            id.AttemptNumber, id.StepName, id.RunNumber, id.PipelineId, finished.Status, (long?)finished.Elapsed?.TotalMilliseconds);
    }

    private void LogIfFailed(Result result, HarnessAttemptIdentity id)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of attempt {AttemptNumber} of step '{StepName}' (run #{RunNumber}, pipeline '{PipelineId}'; it may have been deleted)",
                id.AttemptNumber, id.StepName, id.RunNumber, id.PipelineId);
    }
}
