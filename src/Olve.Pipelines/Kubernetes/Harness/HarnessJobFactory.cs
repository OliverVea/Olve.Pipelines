using System.Text.Json;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Failures;
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
            new HarnessUrls(stepUrl, inputUrl, resultUrl, fileUrls), step.ClusterAccess);
        return HarnessJobBuilder.Build(request, options);
    }

    /// <summary>
    /// A failure handler's Job: <c>run &lt;handler&gt;</c> from the failed run's pinned binary with the dispatch's
    /// <c>input.json</c> (already at <see cref="FailureHandlerDispatch.InputKey"/>), the handler's declared secrets,
    /// and no result upload (the outcome is read from the log). Labelled <c>olve-role=on-failure</c>.
    /// </summary>
    public async Task<Result<HarnessJobSpec>> CreateFailureHandlerJobAsync(FailureHandlerDispatch dispatch, CancellationToken ct = default)
    {
        var expiry = options.LateUrlExpiry(dispatch.Handler.TimeoutSeconds);
        var stepUrl = await presigner.PresignGetAsync(dispatch.Version.BinaryKey, expiry, ct);
        var inputUrl = await presigner.PresignGetAsync(dispatch.InputKey, expiry, ct);

        var run = dispatch.Run;
        var request = new HarnessJobRequest(
            dispatch.JobName, AttemptId: null, run.PipelineId, run.Id, run.Number, dispatch.Handler.Name, AttemptNumber: 1,
            run.VersionStamp, dispatch.Handler.Image, dispatch.Handler.TimeoutSeconds, dispatch.Handler.Secrets,
            PipelineSecrets.Name(run.PipelineId),
            new HarnessUrls(stepUrl, inputUrl, ResultUrl: null, new Dictionary<string, string>()),
            Role: HarnessJobBuilder.FailureHandlerRole,
            UrlSecretNameOverride: dispatch.UrlSecretName,
            ClusterAccess: dispatch.Handler.ClusterAccess);
        return HarnessJobBuilder.Build(request, options);
    }

    /// <summary>
    /// The build/describe Job for <paramref name="commitSha"/>; the binary is PUT to <see cref="StorageLayout.BuildBinaryKey"/>.
    /// </summary>
    /// <param name="tokenSecretKey">Key of the GitHub token in the pipeline's K8s Secret (the binding's credentials); null = anonymous codeload.</param>
    public async Task<Result<HarnessJobSpec>> CreateBuildJobAsync(
        string jobName, Id<Pipeline> pipelineId, string repo, string commitSha, string pipelinePath,
        string? tokenSecretKey, CancellationToken ct = default)
    {
        var binaryUrl = await presigner.PresignPutAsync(
            StorageLayout.BuildBinaryKey(pipelineId, commitSha),
            TimeSpan.FromSeconds(options.BuildTimeoutSeconds + options.UrlExpiryGraceSeconds), ct);

        return BuildJobBuilder.Build(new BuildJobRequest(
            jobName, pipelineId, repo, commitSha, pipelinePath,
            tokenSecretKey is null ? null : PipelineSecrets.Name(pipelineId),
            binaryUrl, tokenSecretKey), options);
    }

    /// <summary>A map-trigger Job over the version binary at <paramref name="binaryKey"/>; the event must already be at <paramref name="eventKey"/>.</summary>
    public async Task<Result<HarnessJobSpec>> CreateMapTriggerJobAsync(
        string jobName, Id<Pipeline> pipelineId, string binaryKey, string triggerName, string eventKey, CancellationToken ct = default)
    {
        var expiry = TimeSpan.FromSeconds(options.MapTriggerTimeoutSeconds + options.UrlExpiryGraceSeconds);
        var stepUrl = await presigner.PresignGetAsync(binaryKey, expiry, ct);
        var eventUrl = await presigner.PresignGetAsync(eventKey, expiry, ct);
        return HarnessJobBuilder.BuildMapTrigger(new MapTriggerJobRequest(jobName, pipelineId, triggerName, stepUrl, eventUrl), options);
    }
}
