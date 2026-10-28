using System.Text;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>What a successful reconcile did.</summary>
/// <param name="Version">The version the pipeline is now switched to (or draining towards).</param>
/// <param name="Created">True when this reconcile added <paramref name="Version"/>; false when the manifest matched an existing one.</param>
/// <param name="Pending">Set when <paramref name="Version"/> was not activated: its config or secrets aren't satisfied yet.</param>
public sealed record ReconcileReport(
    PipelineVersion Version,
    bool Created,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ChangedSteps,
    IReadOnlyList<string> ChangedModels,
    PendingVersion? Pending = null);

/// <summary>
/// v2 reconcile of one config commit (spec §5.1, §6): build/describe Job (compile the pipeline at the
/// commit, PUT <c>step.tar.gz</c> to <see cref="StorageLayout.BuildBinaryKey"/>, print the manifest
/// between sentinels) → parse → validate (errors fail the reconcile; warnings are reported) → entity
/// versions over the persisted history → a new <see cref="PipelineVersion"/> when the manifest hash
/// differs from the active one → the activation gate (<see cref="ConfigActivationService.Activate"/>: switch, or
/// pending until the config document and referenced secrets satisfy it). The build is the async I/O part;
/// <see cref="Apply"/> is the synchronous domain part.
/// </summary>
public class PipelineReconcileService(
    HarnessJobFactory factory,
    HarnessJobControl control,
    IHarnessStorage storage,
    PipelineVersionService versions,
    PipelineActivationService activations,
    ConfigActivationService gate,
    IPipelineSecretNames secretNames,
    PipelineService pipelines,
    AttachmentStore<Pipeline, EntityVersionMap> history,
    RunSchedulingLock schedulingLock,
    HarnessOptions options,
    ILogger<PipelineReconcileService> logger) : IConfigReconciler
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    private const int LogTailLines = 40;

    public async Task<Result<ReconcileReport>> ReconcileAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var built = await BuildAsync(binding, commitSha, ct);
        if (built.TryPickProblems(out var buildProblems, out var manifestText))
            return Result.Failure<ReconcileReport>(buildProblems);

        var setSecrets = await secretNames.ListSetAsync(binding.PipelineId, ct);
        var applied = Apply(binding.PipelineId, commitSha, manifestText, setSecrets);
        if (applied.TryPickProblems(out _, out var report))
            return applied; // logged by the caller, like a failed build

        logger.LogInformation("Reconciled '{Repo}@{Sha}' ({Path}) for pipeline '{PipelineId}' in {ElapsedMs}ms: version {VersionNumber} ({Outcome}{Pending}); changed steps {ChangedSteps}, models {ChangedModels}",
            binding.Repo, commitSha, binding.Path, binding.PipelineId, sw.ElapsedMilliseconds, report.Version.Number,
            report.Created ? "new" : "unchanged", report.Pending is null ? "" : ", pending",
            string.Join(",", report.ChangedSteps), string.Join(",", report.ChangedModels));
        return applied;
    }

    /// <summary>
    /// The domain half: manifest text → validated version → activation gate. Pure apart from the stores.
    /// <paramref name="setSecrets"/>: the pipeline's set secret names (null = unknown).
    /// </summary>
    public Result<ReconcileReport> Apply(Id<Pipeline> pipelineId, string commitSha, string manifestText, IReadOnlySet<string>? setSecrets = null)
    {
        if (!ManifestParser.TryParse(Encoding.UTF8.GetBytes(manifestText), out var manifest, out var parseIssues))
            return Result.Failure<ReconcileReport>(parseIssues.Select(i => new ResultProblem("manifest {0}", i.ToString())));

        var validation = ManifestValidator.Validate(manifest);
        var warnings = validation.Warnings.Select(i => i.ToString()).ToList();
        if (validation.Errors.Count > 0)
            return Result.Failure<ReconcileReport>(validation.Errors.Select(i => new ResultProblem("{0}", i.ToString())));

        return schedulingLock.Run<Result<ReconcileReport>>(() =>
        {
            history.TryGet(pipelineId, out var previous);
            var computed = ManifestVersioning.Compute(previous, manifest);

            PipelineVersion target;
            var created = false;
            var current = CurrentTarget(pipelineId);
            if (current is not null && current.ManifestHash == computed.ManifestHash)
            {
                target = current;
            }
            else if (versions.TryGetLatest(pipelineId, out var latest) && latest.ManifestHash == computed.ManifestHash)
            {
                target = latest;
            }
            else
            {
                var added = versions.Add(
                    pipelineId, computed.CanonicalManifest, computed.ManifestHash, commitSha,
                    computed.Current.Steps.ToDictionary(kv => kv.Key, kv => kv.Value.Version, StringComparer.Ordinal),
                    computed.Current.Models.ToDictionary(kv => kv.Key, kv => kv.Value.Version, StringComparer.Ordinal),
                    StorageLayout.BuildBinaryKey(pipelineId, commitSha));
                if (added.TryPickProblems(out var addProblems, out target!))
                    return Result.Failure<ReconcileReport>(addProblems);
                history.Set(pipelineId, computed.History);
                created = true;
            }

            PendingVersion? pendingVersion = null;
            if (target.Id == current?.Id)
            {
                // Back on the active version: a newer pending one is moot (a held active version stays held).
                if (gate.TryGetPending(pipelineId, out var marker) && marker!.VersionId != target.Id)
                    gate.ClearPending(pipelineId);
            }
            else
            {
                if (gate.Activate(target, setSecrets).TryPickProblems(out var gateProblems, out var outcome))
                    return Result.Failure<ReconcileReport>(gateProblems);
                pendingVersion = (outcome as ActivationOutcome.Pending)?.Version;
                if (outcome is ActivationOutcome.Activated { ChangedDefaults.Count: > 0 } activated)
                    warnings.AddRange(activated.ChangedDefaults.Select(d => $"v{target.Number} changes the default of {d.Path} ({d.From} → {d.To})"));
            }

            if (pipelines.SetDefinition(pipelineId, manifest.Name, manifest.Description).TryPickProblems(out var nameProblems))
                return Result.Failure<ReconcileReport>(nameProblems);

            return new ReconcileReport(target, created, warnings,
                created ? computed.ChangedSteps : [], created ? computed.ChangedModels : [], pendingVersion);
        });
    }

    /// <summary>The version new runs bind to: the active one, or the drain target.</summary>
    private PipelineVersion? CurrentTarget(Id<Pipeline> pipelineId)
    {
        if (!activations.TryGet(pipelineId, out var activation))
            return null;
        var id = activation switch
        {
            PipelineActivation.Active a => a.VersionId,
            PipelineActivation.Draining d => d.ToVersionId,
            _ => throw new InvalidOperationException($"Unknown activation state '{activation.GetType().Name}'."),
        };
        return versions.TryGet(id, out var version) ? version : null;
    }

    /// <summary>Deterministic per (pipeline, commit): a restarted reconcile reattaches to (or reuses) the same build.</summary>
    public static string BuildJobName(Id<Pipeline> pipelineId, string commitSha)
        => $"olve-build-{pipelineId.Value.Value.ToString("N")[..12]}-{commitSha[..12]}";

    /// <summary>Runs (or reattaches to / reuses) the build/describe Job; returns the manifest text.</summary>
    private async Task<Result<string>> BuildAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct)
    {
        var jobName = BuildJobName(binding.PipelineId, commitSha);
        var urlSecret = BuildJobBuilder.UrlSecretName(jobName);

        var existing = await control.GetStateAsync(jobName, ct);
        if (existing is not null && HarnessExitCodes.IsFinished(existing))
        {
            var reusable = existing.Main is { ExitCode: 0 }
                           && await storage.ExistsAsync(StorageLayout.BuildBinaryKey(binding.PipelineId, commitSha), ct);
            if (reusable)
            {
                logger.LogInformation("Reusing finished build Job '{JobName}' of '{Repo}@{Sha}'", jobName, binding.Repo, commitSha);
                return await ReadManifestAsync(jobName, existing, ct);
            }

            // A failed build of this commit: rebuild from scratch (the operator asked for a retry).
            await control.DeleteJobAsync(jobName);
            if (!await WaitGoneAsync(jobName, ct))
                return new UnavailableProblem("The previous build Job '{0}' of this commit is still being deleted; retry shortly.", jobName);
            existing = null;
        }

        if (existing is null)
        {
            var spec = await factory.CreateBuildJobAsync(
                jobName, binding.PipelineId, binding.Repo, commitSha, binding.Path, binding.CredentialsSecret, ct);
            if (spec.TryPickProblems(out var specProblems, out var built))
                return Result.Failure<string>(specProblems);

            await control.SubmitAsync(built, ct);
            logger.LogInformation("Started build Job '{JobName}' for '{Repo}@{Sha}' ({Path}), pipeline '{PipelineId}'",
                jobName, binding.Repo, commitSha, binding.Path, binding.PipelineId);
        }
        else
        {
            logger.LogInformation("Reattaching to build Job '{JobName}' of '{Repo}@{Sha}'", jobName, binding.Repo, commitSha);
        }

        try
        {
            var wait = await control.WaitAsync(jobName, PollInterval, shouldAbort: null,
                TimeSpan.FromSeconds(options.BuildTimeoutSeconds + options.StartupGraceSeconds), ct);

            switch (wait)
            {
                case HarnessWait.Finished finished:
                    return await ReadManifestAsync(jobName, finished.State, ct);
                case HarnessWait.TimedOut:
                    await control.DeleteJobAsync(jobName);
                    return new ResultProblem("The build/describe Job '{0}' did not finish in time.", jobName);
                default:
                    return new ResultProblem("The build/describe Job '{0}' disappeared before it finished.", jobName);
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                await control.DeleteSecretAsync(urlSecret);
        }
    }

    private async Task<Result<string>> ReadManifestAsync(string jobName, HarnessPodState state, CancellationToken ct)
    {
        var log = await control.TryGetLogsAsync(jobName, BuildJobBuilder.ContainerName, ct);

        if (state.Main is { ExitCode: 0 })
        {
            return HarnessSentinels.TryExtractManifest(log) is { } manifest
                ? manifest
                : new ResultProblem("The build/describe Job '{0}' succeeded but printed no manifest.", jobName);
        }

        var reason = state.Main switch
        {
            { ExitCode: 65 } => "the repository tarball could not be fetched (exit 65)",
            { ExitCode: 78 } => "the pipeline is invalid (exit 78)",
            { ExitCode: 64 } => "olve-build usage error (exit 64)",
            { ExitCode: 1 } => "the build failed (exit 1)",
            { ExitCode: { } code } => $"the build exited {code}",
            _ => HarnessExitCodes.Classify(state)?.Message ?? "the build did not run",
        };
        return new ResultProblem("Build/describe failed: {0}.{1}", reason, Tail(log));
    }

    private async Task<bool> WaitGoneAsync(string jobName, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            if (await control.GetStateAsync(jobName, ct) is null)
                return true;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }

    private static string Tail(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return "";
        var lines = log.TrimEnd('\n').Split('\n');
        return "\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - LogTailLines)));
    }
}
