using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>One build/describe Job to run: its deterministic name and role, and what it builds.</summary>
public sealed record BuildJobTarget(
    string JobName,
    string Role,
    Id<Pipeline> PipelineId,
    string Repo,
    string CommitSha,
    string PipelinePath,
    string? CredentialsSecret);

/// <summary>
/// Runs a build/describe Job to the end and returns the manifest it printed: it reattaches to a Job of the same name
/// (a restart mid-build), reuses a finished successful one whose binary exists, and rebuilds a failed one. Shared by
/// reconcile and a pull request's own CI definition, which differ only in the Job's name and role. Transient.
/// </summary>
public class BuildJobRunner(
    HarnessJobFactory factory,
    HarnessJobControl control,
    IHarnessStorage storage,
    HarnessOptions options,
    ILogger<BuildJobRunner> logger)
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    internal TimeSpan DeletePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    private const int LogTailLines = 40;

    public async Task<Result<string>> RunAsync(BuildJobTarget target, CancellationToken ct)
    {
        var jobName = target.JobName;
        var urlSecret = BuildJobBuilder.UrlSecretName(jobName);

        var existing = await control.GetStateAsync(jobName, ct);
        if (existing is not null && HarnessExitCodes.IsFinished(existing))
        {
            var reusable = existing.Main is { ExitCode: 0 }
                           && await storage.ExistsAsync(StorageLayout.BuildBinaryKey(target.PipelineId, target.CommitSha), ct);
            if (reusable)
            {
                logger.LogInformation("Reusing finished build Job '{JobName}' of '{Repo}@{Sha}'", jobName, target.Repo, target.CommitSha);
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
                jobName, target.PipelineId, target.Repo, target.CommitSha, target.PipelinePath, target.CredentialsSecret, ct, target.Role);
            if (spec.TryPickProblems(out var specProblems, out var built))
                return Result.Failure<string>(specProblems);

            await control.SubmitAsync(built, ct);
            logger.LogInformation("Started build Job '{JobName}' for '{Repo}@{Sha}' ({Path}), pipeline '{PipelineId}'",
                jobName, target.Repo, target.CommitSha, target.PipelinePath, target.PipelineId);
        }
        else
        {
            logger.LogInformation("Reattaching to build Job '{JobName}' of '{Repo}@{Sha}'", jobName, target.Repo, target.CommitSha);
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

    /// <summary>Stops a build nobody waits for any more: its Job and URL Secret go.</summary>
    public async Task DeleteAsync(string jobName)
    {
        await control.DeleteJobAsync(jobName);
        await control.DeleteSecretAsync(BuildJobBuilder.UrlSecretName(jobName));
    }

    public Task<bool> BinaryExistsAsync(Id<Pipeline> pipelineId, string commitSha, CancellationToken ct) =>
        storage.ExistsAsync(StorageLayout.BuildBinaryKey(pipelineId, commitSha), ct);

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
            await Task.Delay(DeletePollInterval, ct);
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
