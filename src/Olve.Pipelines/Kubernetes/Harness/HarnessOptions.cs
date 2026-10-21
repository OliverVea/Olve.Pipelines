using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Settings for v2 step Jobs (harness pod) and build/describe Jobs. Bound from the <c>Harness</c>
/// config section (<c>Harness__HarnessBaseImage</c> etc.); runtime class and namespace come from the
/// existing <c>Kubernetes</c> settings.
/// </summary>
public sealed record HarnessOptions
{
    /// <summary>The olve-init image. Pinned by content tag — never <c>latest</c> (harness/README.md).</summary>
    public string HarnessBaseImage { get; init; } = "registry.ovea.pro/olve-pipelines-v2/harness-base:0c4808c152bf";

    /// <summary>Build/describe image: glibc Bun (never alpine), the Bun version the SDK pins.</summary>
    public string BunImage { get; init; } = "oven/bun:1.4.3-slim";

    /// <summary>Pod <c>runtimeClassName</c> (gvisor on the cluster); null = plain runc.</summary>
    public string? RuntimeClassName { get; init; }

    /// <summary>A step without <c>timeoutSeconds</c> gets this.</summary>
    public long DefaultStepTimeoutSeconds { get; init; } = 3600;

    /// <summary>Upper bound on any step's timeout.</summary>
    public long MaxStepTimeoutSeconds { get; init; } = 6 * 3600;

    /// <summary>
    /// Added to the step timeout for <c>activeDeadlineSeconds</c>, which counts from Job start and so
    /// also covers image pulls and olve-init.
    /// </summary>
    public long StartupGraceSeconds { get; init; } = 120;

    /// <summary>Extra validity of URLs used at the end of the step (result PUT, file GETs) beyond the deadline.</summary>
    public long UrlExpiryGraceSeconds { get; init; } = 600;

    /// <summary>K8s deletes a finished Job after this (the controller copies logs/results first).</summary>
    public int TtlSecondsAfterFinished { get; init; } = KubernetesClient.StepJobTtlSeconds;

    /// <summary><c>sizeLimit</c> of the step pod's <c>olve</c> emptyDir (binary ~91 MB + work + files).</summary>
    public string ScratchSizeLimit { get; init; } = "1Gi";

    /// <summary>Optional step container resources; null leaves them unset.</summary>
    public string? StepCpuRequest { get; init; }
    public string? StepMemoryRequest { get; init; }
    public string? StepMemoryLimit { get; init; }

    /// <summary>Build/describe Job deadline.</summary>
    public long BuildTimeoutSeconds { get; init; } = 900;

    /// <summary><c>sizeLimit</c> of the build Job's <c>work</c> emptyDir (repo + node_modules + binary twice).</summary>
    public string BuildScratchSizeLimit { get; init; } = "4Gi";

    public string BuildCpuRequest { get; init; } = "500m";
    public string BuildMemoryRequest { get; init; } = "512Mi";
    public string BuildMemoryLimit { get; init; } = "3Gi";

    /// <summary>Key in the pipeline Secret holding the GitHub token used to fetch the repo tarball (optional).</summary>
    public string BuildTokenSecretKey { get; init; } = "GITHUB_TOKEN";

    /// <summary><c>activeDeadlineSeconds</c> of a map-trigger Job (mappers are pure and fast).</summary>
    public long MapTriggerTimeoutSeconds { get; init; } = 300;

    /// <summary>Map-trigger Jobs are collected after a day (their outcome is in the service log and the run).</summary>
    public int MapTriggerTtlSeconds { get; init; } = 24 * 3600;

    /// <summary>Caps on what a step's <c>result.tar.gz</c> may contain.</summary>
    public HarnessResultLimits ResultLimits { get; init; } = new();

    /// <summary><c>activeDeadlineSeconds</c> for a step: timeout (default/capped) + startup grace.</summary>
    public long DeadlineSeconds(long? stepTimeoutSeconds)
    {
        var timeout = stepTimeoutSeconds is > 0 ? stepTimeoutSeconds.Value : DefaultStepTimeoutSeconds;
        return Math.Min(timeout, MaxStepTimeoutSeconds) + StartupGraceSeconds;
    }

    /// <summary>Expiry for URLs a step uses late (result PUT, file GETs): the whole deadline plus grace.</summary>
    public TimeSpan LateUrlExpiry(long? stepTimeoutSeconds)
        => TimeSpan.FromSeconds(DeadlineSeconds(stepTimeoutSeconds) + UrlExpiryGraceSeconds);
}

/// <summary>Size caps for result collection. Step outputs are untrusted (spec §8).</summary>
public sealed record HarnessResultLimits
{
    /// <summary>Largest <c>result.tar.gz</c> accepted (compressed).</summary>
    public long MaxArchiveBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Largest total uncompressed content (guards gzip bombs).</summary>
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Most entries (files + directories) in the archive.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>
    /// Largest <c>output.json</c> envelope (canonical, 2-space indented — so well above the compact cap).
    /// The compacted <c>output</c> value itself is capped at <see cref="AttemptService.MaxOutputJsonBytes"/>.
    /// </summary>
    public int MaxEnvelopeBytes { get; init; } = HarnessEnvelope.DefaultMaxBytes;
}

/// <summary>Garage keys the harness executor uses beyond <see cref="StorageLayout"/> (all under the attempt prefix).</summary>
public static class HarnessStorageKeys
{
    /// <summary>The assembled step input that olve-init fetches as <c>/olve/input.json</c>.</summary>
    public static string AttemptInputKey(Attempt attempt) => $"{StorageLayout.AttemptPrefix(attempt)}/input.json";

    /// <summary>Where the harness PUTs <c>result.tar.gz</c> (via <c>OLVE_RESULT_URL</c>) before the service unpacks it.</summary>
    public static string AttemptResultArchiveKey(Attempt attempt) => $"{StorageLayout.AttemptPrefix(attempt)}/result.tar.gz";

    /// <summary>The stored object of a file ref: <c>runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/files/&lt;object path&gt;</c>.</summary>
    public static string FileObjectKey(Id<Pipeline> pipelineId, HarnessFileRef fileRef)
        => $"{StorageLayout.AttemptPrefix(pipelineId, fileRef.Run, fileRef.Step, fileRef.Attempt)}/files/{fileRef.ObjectPath}";

    /// <summary>Per-attempt Secret carrying the pre-signed URLs (they embed the access key id).</summary>
    public static string UrlSecretName(Id<Attempt> attemptId) => $"olve-urls-{attemptId.Value.Value:N}";
}
