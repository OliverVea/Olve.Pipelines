using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>Why an attempt failed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptFailureKind>))]
public enum AttemptFailureKind
{
    /// <summary>Exit 1: the handler threw.</summary>
    Handler,
    /// <summary>Exit 65 (step): <c>input.json</c> missing or invalid for the step.</summary>
    Input,
    /// <summary>Exit 66 (step): the return value failed strict validation, or an own file ref is missing; or the server rejected the output.</summary>
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
    /// <summary>The pod could not start a container (image pull, missing secret/key, …), or the Job failed without a container verdict.</summary>
    PodFailed,
    /// <summary>The collected result was missing, malformed or over a size cap.</summary>
    ResultInvalid,
    /// <summary>The step container exited with a code the harness doesn't define.</summary>
    Unknown,
    /// <summary>The K8s Job vanished while the attempt ran, so its outcome is unknown.</summary>
    JobLost,
    /// <summary>The server could not launch the attempt (binary, Job spec, K8s submission).</summary>
    LaunchFailed,
    /// <summary>The scheduler failed the attempt without running it (e.g. its input exceeds the cap).</summary>
    NotRun,
    /// <summary>Someone declined the step's external input.</summary>
    Declined,
}

/// <summary>Who a failure says something about.</summary>
public enum AttemptFailureCategory
{
    /// <summary>The step ran and reported a failure: possibly a real problem it found.</summary>
    Step,
    /// <summary>The platform around the step failed (pod, image pull, launch, storage).</summary>
    Infrastructure,
    /// <summary>The step hit its timeout. Reported on its own, and counted with infrastructure as no real reason.</summary>
    Timeout,
    /// <summary>The step never ran, or a person decided: no statement about the step at all.</summary>
    NotAnOutcome,
}

public static class AttemptFailures
{
    public static AttemptFailureCategory Category(AttemptFailureKind kind) => kind switch
    {
        AttemptFailureKind.Handler or AttemptFailureKind.Input or AttemptFailureKind.Output or AttemptFailureKind.ResultInvalid
            or AttemptFailureKind.PipelineInvalid or AttemptFailureKind.Unknown => AttemptFailureCategory.Step,
        AttemptFailureKind.DeadlineExceeded => AttemptFailureCategory.Timeout,
        AttemptFailureKind.NotRun or AttemptFailureKind.Declined => AttemptFailureCategory.NotAnOutcome,
        _ => AttemptFailureCategory.Infrastructure,
    };

    private static readonly (string Prefix, AttemptFailureKind Kind)[] ReasonPrefixes =
    [
        ("step timed out", AttemptFailureKind.DeadlineExceeded),
        ("olve-init failed", AttemptFailureKind.InitFailed),
        ("container '", AttemptFailureKind.PodFailed),
        ("Job failed", AttemptFailureKind.PodFailed),
        ("step container did not terminate", AttemptFailureKind.PodFailed),
        ("step container was killed", AttemptFailureKind.OutOfMemory),
        ("unknown harness mode", AttemptFailureKind.Usage),
        ("the result upload failed", AttemptFailureKind.ResultUpload),
        ("the handler was interrupted", AttemptFailureKind.Cancelled),
        ("K8s Job ", AttemptFailureKind.JobLost),
        ("K8s submission failed", AttemptFailureKind.LaunchFailed),
        ("cannot launch", AttemptFailureKind.LaunchFailed),
        ("cannot build the step Job", AttemptFailureKind.LaunchFailed),
        ("declined by ", AttemptFailureKind.Declined),
        ("input.json exceeds", AttemptFailureKind.NotRun),
        ("Input of step", AttemptFailureKind.NotRun),
    ];

    /// <summary>The attempt's failure kind, read from the fixed start of its reason when it was recorded without one (no match: <see cref="AttemptFailureKind.Handler"/>).</summary>
    public static AttemptFailureKind KindOf(AttemptStatus.Failed failed)
        => failed.Kind ?? ReasonPrefixes.FirstOrDefault(p => failed.Reason?.StartsWith(p.Prefix, StringComparison.Ordinal) == true).Kind;
}
