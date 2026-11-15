using System.Globalization;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Where one attempt's harness Job lives: its K8s names and its storage prefix, plus what its pod's environment and
/// labels say about it. The executor, the Job factory and the result collector work from this, not from the kind of
/// attempt that owns it.
/// </summary>
/// <param name="JobName">The K8s Job name; deterministic, because a restarted executor reattaches by it.</param>
/// <param name="UrlSecretName">The Secret holding the Job's pre-signed URLs.</param>
/// <param name="Prefix">The attempt's storage prefix: <c>input.json</c>, <c>result.tar.gz</c>, <c>log</c>, <c>output.json</c> and <c>files/</c> live under it.</param>
/// <param name="RunId">The run's id as the pod sees it (<c>OLVE_RUN_ID</c>).</param>
/// <param name="StepName">The step or matrix cell name.</param>
/// <param name="InPreflight">The Job runs the step's preflight (no result upload).</param>
public sealed record HarnessAttemptIdentity(
    string JobName,
    string UrlSecretName,
    string Prefix,
    Id<Pipeline> PipelineId,
    string RunId,
    int RunNumber,
    string StepName,
    int AttemptNumber,
    bool InPreflight)
{
    /// <summary>The Job's <c>olve-role</c> label: <c>step</c> for CD attempts, <c>ci-step</c> for CI's.</summary>
    public string Role { get; init; } = HarnessJobBuilder.StepRole;

    /// <summary>The storage key of the object a file ref in this attempt's input points at.</summary>
    public Func<HarnessFileRef, string>? FileObjectKeyOverride { get; init; }

    public string FileObjectKey(HarnessFileRef fileRef) => FileObjectKeyOverride?.Invoke(fileRef) ?? HarnessStorageKeys.FileObjectKey(PipelineId, fileRef);

    public string InputKey => $"{Prefix}/input.json";

    public string ResultArchiveKey => $"{Prefix}/result.tar.gz";

    public string LogKey => $"{Prefix}/log";

    public string OutputKey => $"{Prefix}/output.json";

    public string FilesPrefix => $"{Prefix}/files";

    /// <summary>A CD attempt's identity: <c>olve-&lt;id&gt;</c> (or its preflight's) under <c>runs/</c>.</summary>
    public static HarnessAttemptIdentity Of(Attempt attempt) => new(
        attempt.KubernetesJobName,
        HarnessStorageKeys.UrlSecretName(attempt),
        StorageLayout.AttemptPrefix(attempt),
        attempt.PipelineId,
        attempt.RunId.Value.Value.ToString("D", CultureInfo.InvariantCulture),
        attempt.RunNumber,
        attempt.StepName,
        attempt.Number,
        attempt.InPreflight);

    /// <summary>A CI attempt's identity: <c>olve-ci-&lt;id&gt;</c> under <c>ci/</c>; its input's file refs point at the same CI run.</summary>
    public static HarnessAttemptIdentity Of(CiAttempt attempt) => new(
        attempt.KubernetesJobName,
        attempt.UrlSecretName,
        StorageLayout.CiAttemptPrefix(attempt.PipelineId, attempt.RunNumber, attempt.StepName, attempt.Number),
        attempt.PipelineId,
        attempt.RunId.Value.Value.ToString("D", CultureInfo.InvariantCulture),
        attempt.RunNumber,
        attempt.StepName,
        attempt.Number,
        InPreflight: false)
    {
        Role = HarnessJobBuilder.CiStepRole,
        FileObjectKeyOverride = fileRef =>
            $"{StorageLayout.CiAttemptPrefix(attempt.PipelineId, fileRef.Run, fileRef.Step, fileRef.Attempt)}/files/{fileRef.ObjectPath}",
    };
}
