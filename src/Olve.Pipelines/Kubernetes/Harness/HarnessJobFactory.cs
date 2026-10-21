using System.Text.Json;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared.Storage;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Pre-signs the URLs a harness/build Job needs and builds its spec. The async glue between
/// <see cref="S3UrlPresigner"/> and the pure <see cref="HarnessJobBuilder"/> / <see cref="BuildJobBuilder"/>.
/// The input must already be stored at <see cref="HarnessStorageKeys.AttemptInputKey"/> before the Job runs.
/// </summary>
public sealed class HarnessJobFactory(S3UrlPresigner presigner, HarnessOptions options)
{
    /// <param name="binaryKey">The pinned version's <c>step.tar.gz</c> (<see cref="PipelineVersion.BinaryKey"/>).</param>
    /// <param name="step">The step from that version's manifest (image, timeout, secrets).</param>
    /// <param name="input">The attempt's assembled <c>input.json</c>, scanned for file refs (→ <c>OLVE_FILE_URLS</c>); null = no input file.</param>
    public async Task<Result<HarnessJobSpec>> CreateStepJobAsync(
        Attempt attempt, string binaryKey, string versionStamp, StepDescriptor step, JsonElement? input,
        CancellationToken ct = default)
    {
        var lateExpiry = options.LateUrlExpiry(step.TimeoutSeconds);

        var stepUrl = await presigner.PresignGetAsync(binaryKey, ct: ct);
        var inputUrl = input is null ? null : await presigner.PresignGetAsync(HarnessStorageKeys.AttemptInputKey(attempt), ct: ct);
        var resultUrl = await presigner.PresignPutAsync(HarnessStorageKeys.AttemptResultArchiveKey(attempt), lateExpiry, ct);

        IReadOnlyDictionary<string, string> fileUrls = new Dictionary<string, string>();
        if (input is { } json)
        {
            var map = await HarnessFileRefs.BuildUrlMapAsync(attempt.PipelineId, HarnessFileRefs.Find(json), presigner, lateExpiry, ct);
            if (map.TryPickProblems(out var problems, out var urls))
                return Result.Failure<HarnessJobSpec>(problems);
            fileUrls = urls;
        }

        var request = HarnessJobRequest.ForAttempt(
            attempt, versionStamp, step.Image, step.TimeoutSeconds, step.Secrets,
            new HarnessUrls(stepUrl, inputUrl, resultUrl, fileUrls));
        return HarnessJobBuilder.Build(request, options);
    }

    /// <param name="hasPipelineSecret">Whether the pipeline has a K8s Secret (its token key, if present, authenticates the fetch).</param>
    public async Task<Result<HarnessJobSpec>> CreateBuildJobAsync(
        string jobName, Id<Pipeline> pipelineId, int versionNumber, string repo, string commitSha, string pipelinePath,
        bool hasPipelineSecret, CancellationToken ct = default)
    {
        var binaryUrl = await presigner.PresignPutAsync(
            StorageLayout.VersionBinaryKey(pipelineId, versionNumber),
            TimeSpan.FromSeconds(options.BuildTimeoutSeconds + options.UrlExpiryGraceSeconds), ct);

        return BuildJobBuilder.Build(new BuildJobRequest(
            jobName, pipelineId, repo, commitSha, pipelinePath,
            hasPipelineSecret ? HarnessJobBuilder.PipelineSecretName(pipelineId) : null,
            binaryUrl), options);
    }
}
