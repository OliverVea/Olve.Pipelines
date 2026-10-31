using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

public sealed record HarnessFailure(AttemptFailureKind Kind, string Message);

/// <summary>One container's last observed state.</summary>
/// <param name="ExitCode">Set once terminated.</param>
/// <param name="Reason">Terminated reason (<c>Completed</c>, <c>Error</c>, <c>OOMKilled</c>) or waiting reason (<c>ErrImagePull</c>, <c>CreateContainerConfigError</c>, …).</param>
/// <param name="StartedAt">When the container started, once terminated.</param>
public sealed record HarnessContainerState(string Name, int? ExitCode, string? Reason, string? Message,
    DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null)
{
    public bool Terminated => ExitCode is not null;
}

/// <summary>
/// What the executor needs to classify a finished (or stuck) harness/build pod: the Job's failure
/// condition and the init / main container states. Built by
/// <see cref="IHarnessKubernetesClient.GetHarnessPodStateAsync"/>, or directly in tests.
/// </summary>
/// <param name="JobFailedReason">Reason of the Job's <c>Failed=True</c> condition (<c>DeadlineExceeded</c>, <c>BackoffLimitExceeded</c>).</param>
/// <param name="PodCreatedAt">When the pod was created.</param>
public sealed record HarnessPodState(
    string? JobFailedReason,
    string? JobFailedMessage,
    HarnessContainerState? Init,
    HarnessContainerState? Main,
    DateTimeOffset? PodCreatedAt = null)
{
    public static HarnessPodState From(K8sJobResponse? job, K8sPodItem? pod)
    {
        var failed = job?.Status?.Conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
        return new HarnessPodState(
            failed?.Reason,
            failed?.Message,
            ToState(pod?.Status?.InitContainerStatuses?.FirstOrDefault()),
            ToState(pod?.Status?.ContainerStatuses?.FirstOrDefault()),
            pod?.Metadata.CreationTimestamp);
    }

    private static HarnessContainerState? ToState(K8sContainerStatus? status)
    {
        if (status is null) return null;
        if (status.State?.Terminated is { } t)
            return new HarnessContainerState(status.Name, t.ExitCode, t.Reason, t.Message, t.StartedAt, t.FinishedAt);
        return new HarnessContainerState(status.Name, null, status.State?.Waiting?.Reason, status.State?.Waiting?.Message);
    }
}

/// <summary>Maps exit codes and pod/Job state to a <see cref="HarnessFailure"/> (src/sdk/README.md "Exit codes", src/harness/README.md olve-init codes).</summary>
public static class HarnessExitCodes
{
    /// <summary>Waiting reasons that mean the container will never start without intervention.</summary>
    private static readonly HashSet<string> FatalWaitingReasons = new(StringComparer.Ordinal)
    {
        "ErrImagePull", "ImagePullBackOff", "InvalidImageName", "CreateContainerConfigError", "CreateContainerError",
    };

    /// <summary>
    /// The pod has reached a verdict: the Job failed (deadline, backoff), the step container terminated,
    /// or olve-init failed (restartPolicy Never, so the step never starts). Only then is
    /// <see cref="Classify"/> meaningful — on a running pod it reports "did not terminate".
    /// </summary>
    public static bool IsFinished(HarnessPodState state)
        => state.JobFailedReason is not null
           || state.Main is { Terminated: true }
           || state.Init is { Terminated: true, ExitCode: not 0 };

    /// <summary>
    /// A container is stuck in a waiting state that does not heal on its own (image pull failure,
    /// missing Secret key, …). The caller fails the attempt after a short grace and deletes the Job
    /// instead of waiting out <c>activeDeadlineSeconds</c>.
    /// </summary>
    public static bool IsFatalWaiting(HarnessPodState state)
        => new[] { state.Init, state.Main }.Any(c =>
            c is { Terminated: false, Reason: { } reason } && FatalWaitingReasons.Contains(reason));

    /// <summary>
    /// Classifies the pod. Null means the step container exited 0 and nothing else went wrong — the
    /// envelope decides succeeded vs skipped. Init and step exit codes overlap with different meanings,
    /// so which container terminated matters.
    /// </summary>
    public static HarnessFailure? Classify(HarnessPodState state)
    {
        if (state.JobFailedReason == "DeadlineExceeded")
            return new(AttemptFailureKind.DeadlineExceeded, $"step timed out: {state.JobFailedMessage ?? "Job was active longer than its deadline"}");

        if (state.Init is { Terminated: true, ExitCode: not 0 } init)
            return new(init.Reason == "OOMKilled" ? AttemptFailureKind.OutOfMemory : AttemptFailureKind.InitFailed,
                $"olve-init failed: {InitMeaning(init)}");

        foreach (var container in new[] { state.Init, state.Main })
        {
            if (container is { Terminated: false, Reason: { } reason } && FatalWaitingReasons.Contains(reason))
                return new(AttemptFailureKind.PodFailed, $"container '{container.Name}' cannot start: {reason}{Suffix(container.Message)}");
        }

        if (state.Main is { Terminated: true } main)
        {
            if (main.Reason == "OOMKilled" || main.ExitCode == 137)
                return new(AttemptFailureKind.OutOfMemory, $"step container was killed (exit {main.ExitCode}, {main.Reason ?? "SIGKILL"}) — out of memory?");
            return main.ExitCode switch
            {
                0 => null,
                1 => new(AttemptFailureKind.Handler, "the handler threw (exit 1)"),
                64 => new(AttemptFailureKind.Usage, "unknown harness mode or step (exit 64) — binary and manifest disagree"),
                65 => new(AttemptFailureKind.Input, "input.json missing or invalid for the step (exit 65)"),
                66 => new(AttemptFailureKind.Output, "the step's output failed validation (exit 66)"),
                74 => new(AttemptFailureKind.ResultUpload, "the result upload failed after retries (exit 74); output files are lost"),
                78 => new(AttemptFailureKind.PipelineInvalid, "the pipeline is invalid (exit 78)"),
                130 or 143 => new(AttemptFailureKind.Cancelled, $"the handler was interrupted (exit {main.ExitCode})"),
                _ => new(AttemptFailureKind.Unknown, $"step container exited {main.ExitCode}{Suffix(main.Reason)}"),
            };
        }

        if (state.JobFailedReason is not null)
            return new(AttemptFailureKind.PodFailed, $"Job failed ({state.JobFailedReason}){Suffix(state.JobFailedMessage)}");

        return new(AttemptFailureKind.PodFailed, "step container did not terminate");
    }

    private static string InitMeaning(HarnessContainerState init) => init.ExitCode switch
    {
        64 => "bad configuration (exit 64)",
        65 => "downloading the step binary or input failed (exit 65) — expired or wrong pre-signed URL?",
        66 => "the step archive is not a valid tar.gz with 'step' at its root (exit 66)",
        70 => "filesystem error (exit 70)",
        _ => $"exit {init.ExitCode}{Suffix(init.Reason)}",
    };

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}";
}
