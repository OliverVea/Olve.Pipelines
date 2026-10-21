namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>Why a harness attempt failed.</summary>
public enum HarnessFailureKind
{
    /// <summary>Exit 1: the handler threw.</summary>
    Handler,
    /// <summary>Exit 65 (step): <c>input.json</c> missing or invalid for the step.</summary>
    Input,
    /// <summary>Exit 66 (step): the return value failed strict validation, or an own file ref is missing.</summary>
    Output,
    /// <summary>Exit 64 (step): unknown mode or step — a binary/manifest mismatch.</summary>
    Usage,
    /// <summary>Exit 74: <c>result.tar.gz</c> upload failed; the step's files are lost.</summary>
    ResultUpload,
    /// <summary>Exit 78: the pipeline is invalid (describe problems, missing handler module).</summary>
    PipelineInvalid,
    /// <summary>Exit 130/143: the handler was interrupted (SIGINT/SIGTERM).</summary>
    Cancelled,
    /// <summary>A container was OOM-killed.</summary>
    OutOfMemory,
    /// <summary>The Job's <c>activeDeadlineSeconds</c> passed (step timeout).</summary>
    DeadlineExceeded,
    /// <summary>olve-init failed (64 config, 65 download, 66 bad archive, 70 filesystem).</summary>
    InitFailed,
    /// <summary>The pod could not start a container (image pull, missing secret/key, …).</summary>
    PodFailed,
    /// <summary>The collected result was missing, malformed or over a size cap.</summary>
    ResultInvalid,
    /// <summary>Anything else (unexpected exit code, Job failed without a container verdict).</summary>
    Unknown,
}

public sealed record HarnessFailure(HarnessFailureKind Kind, string Message);

/// <summary>One container's last observed state.</summary>
/// <param name="ExitCode">Set once terminated.</param>
/// <param name="Reason">Terminated reason (<c>Completed</c>, <c>Error</c>, <c>OOMKilled</c>) or waiting reason (<c>ErrImagePull</c>, <c>CreateContainerConfigError</c>, …).</param>
public sealed record HarnessContainerState(string Name, int? ExitCode, string? Reason, string? Message)
{
    public bool Terminated => ExitCode is not null;
}

/// <summary>
/// What the executor needs to classify a finished (or stuck) harness/build pod: the Job's failure
/// condition and the init / main container states. Built by
/// <see cref="IHarnessKubernetesClient.GetHarnessPodStateAsync"/>, or directly in tests.
/// </summary>
/// <param name="JobFailedReason">Reason of the Job's <c>Failed=True</c> condition (<c>DeadlineExceeded</c>, <c>BackoffLimitExceeded</c>).</param>
public sealed record HarnessPodState(
    string? JobFailedReason,
    string? JobFailedMessage,
    HarnessContainerState? Init,
    HarnessContainerState? Main)
{
    public static HarnessPodState From(K8sJobResponse? job, K8sPodItem? pod)
    {
        var failed = job?.Status?.Conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
        return new HarnessPodState(
            failed?.Reason,
            failed?.Message,
            ToState(pod?.Status?.InitContainerStatuses?.FirstOrDefault()),
            ToState(pod?.Status?.ContainerStatuses?.FirstOrDefault()));
    }

    private static HarnessContainerState? ToState(K8sContainerStatus? status)
    {
        if (status is null) return null;
        if (status.State?.Terminated is { } t)
            return new HarnessContainerState(status.Name, t.ExitCode, t.Reason, t.Message);
        return new HarnessContainerState(status.Name, null, status.State?.Waiting?.Reason, status.State?.Waiting?.Message);
    }
}

/// <summary>Maps exit codes and pod/Job state to a <see cref="HarnessFailure"/> (sdk/README.md "Exit codes", harness/README.md olve-init codes).</summary>
public static class HarnessExitCodes
{
    /// <summary>Waiting reasons that mean the container will never start without intervention.</summary>
    private static readonly HashSet<string> FatalWaitingReasons = new(StringComparer.Ordinal)
    {
        "ErrImagePull", "ImagePullBackOff", "InvalidImageName", "CreateContainerConfigError", "CreateContainerError",
    };

    /// <summary>
    /// Classifies the pod. Null means the step container exited 0 and nothing else went wrong — the
    /// envelope decides succeeded vs skipped. Init and step exit codes overlap with different meanings,
    /// so which container terminated matters.
    /// </summary>
    public static HarnessFailure? Classify(HarnessPodState state)
    {
        if (state.JobFailedReason == "DeadlineExceeded")
            return new(HarnessFailureKind.DeadlineExceeded, $"step timed out: {state.JobFailedMessage ?? "Job was active longer than its deadline"}");

        if (state.Init is { Terminated: true, ExitCode: not 0 } init)
            return new(init.Reason == "OOMKilled" ? HarnessFailureKind.OutOfMemory : HarnessFailureKind.InitFailed,
                $"olve-init failed: {InitMeaning(init)}");

        foreach (var container in new[] { state.Init, state.Main })
        {
            if (container is { Terminated: false, Reason: { } reason } && FatalWaitingReasons.Contains(reason))
                return new(HarnessFailureKind.PodFailed, $"container '{container.Name}' cannot start: {reason}{Suffix(container.Message)}");
        }

        if (state.Main is { Terminated: true } main)
        {
            if (main.Reason == "OOMKilled" || main.ExitCode == 137)
                return new(HarnessFailureKind.OutOfMemory, $"step container was killed (exit {main.ExitCode}, {main.Reason ?? "SIGKILL"}) — out of memory?");
            return main.ExitCode switch
            {
                0 => null,
                1 => new(HarnessFailureKind.Handler, "the handler threw (exit 1)"),
                64 => new(HarnessFailureKind.Usage, "unknown harness mode or step (exit 64) — binary and manifest disagree"),
                65 => new(HarnessFailureKind.Input, "input.json missing or invalid for the step (exit 65)"),
                66 => new(HarnessFailureKind.Output, "the step's output failed validation (exit 66)"),
                74 => new(HarnessFailureKind.ResultUpload, "the result upload failed after retries (exit 74); output files are lost"),
                78 => new(HarnessFailureKind.PipelineInvalid, "the pipeline is invalid (exit 78)"),
                130 or 143 => new(HarnessFailureKind.Cancelled, $"the handler was interrupted (exit {main.ExitCode})"),
                _ => new(HarnessFailureKind.Unknown, $"step container exited {main.ExitCode}{Suffix(main.Reason)}"),
            };
        }

        if (state.JobFailedReason is not null)
            return new(HarnessFailureKind.Unknown, $"Job failed ({state.JobFailedReason}){Suffix(state.JobFailedMessage)}");

        return new(HarnessFailureKind.Unknown, "step container did not terminate");
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
