namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>How waiting for a harness/build Job ended.</summary>
public abstract record HarnessWait
{
    /// <summary>The pod reached a verdict (<see cref="HarnessExitCodes.IsFinished"/>), or a container was
    /// stuck in a fatal waiting state past the grace (<paramref name="DeletedStuck"/>: the Job was deleted).</summary>
    public sealed record Finished(HarnessPodState State, bool DeletedStuck) : HarnessWait;

    /// <summary>The Job does not exist (deleted, or TTL-collected long ago).</summary>
    public sealed record Missing : HarnessWait;

    /// <summary>The caller's abort check returned true (e.g. the attempt was cancelled).</summary>
    public sealed record Aborted : HarnessWait;

    /// <summary>The caller's overall timeout passed.</summary>
    public sealed record TimedOut : HarnessWait;
}

/// <summary>Container logs of a finished harness pod (null = unavailable, e.g. the container never started).</summary>
public sealed record HarnessLogs(string? Main, string? Init)
{
    /// <summary>The main log, with the init log appended when the main container produced nothing (init failure).</summary>
    public string Combined(string mainContainer)
    {
        var text = Main ?? "";
        if (string.IsNullOrEmpty(Main) && !string.IsNullOrWhiteSpace(Init))
            text += $"--- {HarnessJobBuilder.InitContainerName} ---\n{Init}";
        else if (string.IsNullOrEmpty(Main))
            text += $"(no output from container '{mainContainer}')\n";
        return text;
    }
}

/// <summary>
/// The K8s plumbing shared by step attempts, build/describe Jobs and map-trigger Jobs: submit
/// (URL Secret upsert, then the Job; an existing Job means reattach), wait for a verdict, read
/// container logs, clean up. Never logs Job specs or Secret data (pre-signed URLs).
/// </summary>
public sealed class HarnessJobControl(
    IKubernetesClient kubernetes,
    IHarnessKubernetesClient harness,
    KubernetesOptions options,
    TimeProvider timeProvider,
    PodChangeSignals podChanges,
    ILogger<HarnessJobControl> logger)
{
    /// <summary>How long a fatal waiting reason (ErrImagePull, CreateContainerConfigError, …) may persist before the Job is failed.</summary>
    public TimeSpan FatalWaitingGrace { get; init; } = TimeSpan.FromSeconds(90);

    public string Namespace => options.Namespace;

    public Task<HarnessPodState?> GetStateAsync(string jobName, CancellationToken ct)
        => harness.GetHarnessPodStateAsync(options.Namespace, jobName, ct);

    /// <summary>Upserts the spec's URL Secret, then creates the Job. Returns false when the Job already existed.</summary>
    public async Task<bool> SubmitAsync(HarnessJobSpec spec, CancellationToken ct)
    {
        await harness.UpsertSecretAsync(options.Namespace, spec.UrlSecretName, spec.UrlSecretData, ct);
        return await harness.CreateJobAsync(options.Namespace, spec.Job, ct);
    }

    /// <summary>
    /// Polls until the pod reaches a verdict, waking early when the pod watch reports a change.
    /// A container stuck in a fatal waiting state for longer than
    /// <see cref="FatalWaitingGrace"/> ends the wait too, and its Job is deleted (it would otherwise sit
    /// until <c>activeDeadlineSeconds</c>).
    /// </summary>
    public async Task<HarnessWait> WaitAsync(
        string jobName, TimeSpan pollInterval, Func<bool>? shouldAbort, TimeSpan? timeout, CancellationToken ct)
    {
        var started = timeProvider.GetUtcNow();
        DateTimeOffset? stuckSince = null;

        while (true)
        {
            if (shouldAbort?.Invoke() == true)
                return new HarnessWait.Aborted();

            var state = await GetStateAsync(jobName, ct);
            if (state is null)
                return new HarnessWait.Missing();
            if (HarnessExitCodes.IsFinished(state))
                return new HarnessWait.Finished(state, DeletedStuck: false);

            var now = timeProvider.GetUtcNow();
            if (HarnessExitCodes.IsFatalWaiting(state))
            {
                stuckSince ??= now;
                if (now - stuckSince >= FatalWaitingGrace)
                {
                    logger.LogWarning("K8s Job {JobName} is stuck ({Reason}); deleting it", jobName,
                        HarnessExitCodes.Classify(state)?.Message);
                    await DeleteJobAsync(jobName);
                    return new HarnessWait.Finished(state, DeletedStuck: true);
                }
            }
            else
            {
                stuckSince = null;
            }

            if (timeout is { } t && now - started >= t)
                return new HarnessWait.TimedOut();

            await podChanges.WaitAsync(jobName, pollInterval, ct);
        }
    }

    /// <summary>Best-effort logs of the main container and olve-init (a container that never started yields null).</summary>
    public async Task<HarnessLogs> GetLogsAsync(string jobName, string mainContainer, bool hasInit, CancellationToken ct)
    {
        var main = await TryGetLogsAsync(jobName, mainContainer, ct);
        var init = hasInit ? await TryGetLogsAsync(jobName, HarnessJobBuilder.InitContainerName, ct) : null;
        return new HarnessLogs(main, init);
    }

    public async Task<string?> TryGetLogsAsync(string jobName, string container, CancellationToken ct)
    {
        try
        {
            return await kubernetes.GetPodLogsAsync(options.Namespace, jobName, container, ct);
        }
        catch (HttpRequestException)
        {
            return null; // container not started (400) or pod gone
        }
    }

    public async Task DeleteJobAsync(string jobName)
    {
        try
        {
            await kubernetes.DeleteJobAsync(options.Namespace, jobName, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not delete K8s Job {JobName}", jobName);
        }
    }

    public async Task DeleteSecretAsync(string secretName)
    {
        try
        {
            await kubernetes.DeleteSecretAsync(options.Namespace, secretName, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not delete K8s Secret {SecretName}", secretName);
        }
    }
}
