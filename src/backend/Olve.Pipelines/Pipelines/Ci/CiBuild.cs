using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Ci;

[JsonConverter(typeof(JsonStringEnumConverter<CiBuildStatus>))]
public enum CiBuildStatus
{
    Building,
    Succeeded,
    Failed,
}

/// <summary>
/// A pull request's own CI definition: the build/describe of one tested commit, in a Job of its own
/// (<c>olve-ci-build-&lt;p12&gt;-&lt;sha12&gt;</c>, role <c>ci-build</c>). Its binary is stored where reconcile stores a
/// commit's build (<see cref="StorageLayout.BuildBinaryKey"/>): a build is a function of the commit, so the key is
/// safe to share. It never becomes a <c>PipelineVersion</c>.
/// </summary>
/// <param name="Manifest">The manifest it described, once it succeeded.</param>
/// <param name="Problems">Why it can't be used, once it failed.</param>
public sealed record CiBuild(
    Id<CiBuild> Id,
    Id<Pipeline> PipelineId,
    string CommitSha,
    CiBuildStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null,
    string? Manifest = null,
    IReadOnlyList<string>? Problems = null) : IHasId<Id<CiBuild>>
{
    public const string JobPrefix = "olve-ci-build-";

    [JsonIgnore]
    public string BinaryKey => StorageLayout.BuildBinaryKey(PipelineId, CommitSha);

    /// <summary>Deterministic per (pipeline, commit): a restarted server reattaches to the same build.</summary>
    public static string JobName(Id<Pipeline> pipelineId, string commitSha) =>
        $"{JobPrefix}{pipelineId.Value.Value.ToString("N")[..12]}-{commitSha[..12]}";
}

/// <summary>Runs the build/describe of a commit for CI. The seam between CI and Kubernetes.</summary>
public interface ICiDefinitionBuilder
{
    /// <summary>The manifest the build printed, or why it failed.</summary>
    Task<Result<string>> BuildAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct);

    /// <summary>Stops a build nobody waits for.</summary>
    Task DeleteAsync(Id<Pipeline> pipelineId, string commitSha);

    /// <summary>The commit's binary is still stored (retention may have removed an old one).</summary>
    Task<bool> BinaryExistsAsync(Id<Pipeline> pipelineId, string commitSha, CancellationToken ct);
}

/// <summary>Builds with the same Job reconcile uses, under CI's name and role. Transient.</summary>
public class KubernetesCiDefinitionBuilder(BuildJobRunner runner) : ICiDefinitionBuilder
{
    public Task<Result<string>> BuildAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct) =>
        runner.RunAsync(new BuildJobTarget(CiBuild.JobName(binding.PipelineId, commitSha), BuildJobBuilder.CiBuildRole, binding.PipelineId,
            binding.Repo, commitSha, binding.Path, binding.CredentialsSecret), ct);

    public Task DeleteAsync(Id<Pipeline> pipelineId, string commitSha) => runner.DeleteAsync(CiBuild.JobName(pipelineId, commitSha));

    public Task<bool> BinaryExistsAsync(Id<Pipeline> pipelineId, string commitSha, CancellationToken ct) =>
        runner.BinaryExistsAsync(pipelineId, commitSha, ct);
}

/// <summary>CI builds by (pipeline, commit). Singleton: owns the index.</summary>
public class CiBuildService(EntityStore<CiBuild> store, IdProvider idProvider, TimeProvider timeProvider)
{
    private readonly EntityStoreUniqueIndex<CiBuild, Id<CiBuild>, (Id<Pipeline>, string)> _byCommit =
        store.CreateUniqueIndex<CiBuild, Id<CiBuild>, (Id<Pipeline>, string)>(b => (b.PipelineId, b.CommitSha));

    public bool TryGet(Id<Pipeline> pipelineId, string commitSha, [NotNullWhen(true)] out CiBuild? build)
    {
        build = null;
        return _byCommit.TryGet((pipelineId, commitSha), out var id) && store.TryGet(id, out build);
    }

    /// <summary>A fresh build record of the commit, replacing an earlier one.</summary>
    public CiBuild Start(Id<Pipeline> pipelineId, string commitSha)
    {
        Forget(pipelineId, commitSha);
        var build = new CiBuild(idProvider.Create<CiBuild>(), pipelineId, commitSha, CiBuildStatus.Building, timeProvider.GetUtcNow());
        store.Set(build);
        return build;
    }

    public CiBuild Finish(CiBuild build, string? manifest, IReadOnlyList<string>? problems)
    {
        var finished = build with
        {
            Status = problems is { Count: > 0 } ? CiBuildStatus.Failed : CiBuildStatus.Succeeded,
            FinishedAt = timeProvider.GetUtcNow(),
            Manifest = problems is { Count: > 0 } ? null : manifest,
            Problems = problems is { Count: > 0 } ? problems : null,
        };
        store.Set(finished);
        return finished;
    }

    public void Forget(Id<Pipeline> pipelineId, string commitSha)
    {
        if (TryGet(pipelineId, commitSha, out var existing))
            store.Delete(existing.Id).DiscardResult();
    }

    public IReadOnlyList<CiBuild> List() => store.List();
}

/// <summary>
/// A pull request's own CI definition: builds the tested commit (one build per commit, shared by its runs; a succeeded
/// build whose binary is still stored is reused), then settles the runs that wait for it: a build that fails fails
/// them with its problems, a definition without CI drops them (a pull request that removes CI gets no run), and
/// otherwise their config is pinned and their steps start. Builds run like reconcile's: outside the Job slots.
/// Singleton: holds the builds in flight.
/// </summary>
public class CiDefinitionService(
    IServiceProvider sp,
    CiBuildService builds,
    CiRunService runs,
    CiPlanCache plans,
    CiSchedulingLock schedulingLock,
    ILogger<CiDefinitionService> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Id<Pipeline>, string), (Task Task, CancellationTokenSource Cts)> _inFlight = [];
    private readonly List<Task> _started = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Starts (or joins) the build the run waits for.</summary>
    public void BuildFor(CiRun run)
    {
        if (!run.Building)
            return;
        var key = (run.PipelineId, run.TestedSha);
        lock (_gate)
        {
            if (_inFlight.ContainsKey(key))
                return;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var task = Task.Run(() => BuildAsync(run.PipelineId, run.TestedSha, cts.Token));
            _inFlight[key] = (task, cts);
            _started.Add(task);
        }
    }

    /// <summary>After <paramref name="run"/> ended: stops its build when no other running run waits for it.</summary>
    public void Release(CiRun run)
    {
        if (!run.OwnDefinition || WaitingRuns(run.PipelineId, run.TestedSha).Count > 0)
            return;
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (_inFlight.Remove((run.PipelineId, run.TestedSha), out var entry))
                cts = entry.Cts;
        }

        if (cts is null)
            return;
        cts.Cancel();
        if (builds.TryGet(run.PipelineId, run.TestedSha, out var build) && build.Status is CiBuildStatus.Building)
            builds.Forget(run.PipelineId, run.TestedSha);
        _ = DeleteJobAsync(run.PipelineId, run.TestedSha);
    }

    /// <summary>At startup: the builds that running runs still wait for (their Jobs are reattached by name).</summary>
    public void ResumeBuilding()
    {
        foreach (var run in runs.List().Where(r => r is { Status: RunStatus.Running, Building: true }))
            BuildFor(run);
    }

    /// <summary>Every build started so far, settled; for tests to wait on.</summary>
    internal Task InFlight()
    {
        lock (_gate)
        {
            _started.RemoveAll(t => t.IsCompleted);
            return Task.WhenAll(_started);
        }
    }

    private async Task BuildAsync(Id<Pipeline> pipelineId, string commitSha, CancellationToken ct)
    {
        var builder = sp.GetRequiredService<ICiDefinitionBuilder>();
        CiBuild? build = null;
        try
        {
            if (builds.TryGet(pipelineId, commitSha, out var earlier) && earlier.Status is CiBuildStatus.Succeeded
                && await builder.BinaryExistsAsync(pipelineId, commitSha, ct))
            {
                build = earlier;
            }
            else
            {
                if (sp.GetRequiredService<PipelineConfigBindingService>().GetByPipelineId(pipelineId).TryPickProblems(out var bindingProblems, out var binding))
                {
                    build = builds.Finish(builds.Start(pipelineId, commitSha), null, Problems(bindingProblems));
                }
                else
                {
                    var started = builds.Start(pipelineId, commitSha);
                    var built = await builder.BuildAsync(binding, commitSha, ct);
                    ct.ThrowIfCancellationRequested();
                    build = built.TryPickProblems(out var buildProblems, out var manifest)
                        ? builds.Finish(started, null, Problems(buildProblems))
                        : builds.Finish(started, manifest, Validate(manifest));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Building the CI definition of '{Sha}' in pipeline '{PipelineId}' failed", commitSha, pipelineId);
            build = builds.Finish(builds.Start(pipelineId, commitSha), null, [$"Building the pull request's definition failed: {ex.Message}"]);
        }
        finally
        {
            lock (_gate) _inFlight.Remove((pipelineId, commitSha));
        }

        logger.LogInformation("CI definition of '{Sha}' in pipeline '{PipelineId}' built: {Status}", commitSha, pipelineId, build.Status);
        Settle(build);
    }

    /// <summary>The runs waiting for <paramref name="build"/> go on, fail or are dropped.</summary>
    internal void Settle(CiBuild build) => schedulingLock.Run(() =>
    {
        foreach (var run in WaitingRuns(build.PipelineId, build.CommitSha))
        {
            if (build.Status is CiBuildStatus.Failed)
            {
                runs.FailDefinition(run.Id, build.Problems ?? []).DiscardResult();
                continue;
            }

            if (run.IsQueue && !plans.Manifest(build).TryPickProblems(out _, out var batchManifest) && batchManifest.Ci is null)
            {
                // A batch whose definition drops CI has nothing to check: it lands once built and described.
                logger.LogInformation("Queue run #{RunNumber} of pipeline '{PipelineId}' has a definition without CI: nothing to run",
                    run.Number, run.PipelineId);
                runs.Define(run.Id, null, null).DiscardResult();
                runs.Complete(run.Id, RunStatus.Succeeded).DiscardResult();
                continue;
            }

            if (plans.Manifest(build).TryPickProblems(out _, out var manifest) || manifest.Ci is null)
            {
                logger.LogInformation("Pull request #{Pull} of pipeline '{PipelineId}' removes CI: CI run #{RunNumber} is dropped",
                    run.Pull, run.PipelineId, run.Number);
                runs.Delete(run.Id).DiscardResult();
                continue;
            }

            if (plans.Get(run).TryPickProblems(out var planProblems))
            {
                runs.FailDefinition(run.Id, Problems(planProblems)).DiscardResult();
                continue;
            }

            var problems = new List<string>();
            var config = CiConfig.Pin(run.PipelineId, manifest, "this pull request's definition", sp.GetRequiredService<ConfigSnapshotService>(), problems);
            if (problems.Count > 0)
            {
                runs.FailDefinition(run.Id, problems).DiscardResult();
                continue;
            }

            runs.Define(run.Id, config.Snapshot, config.Config).DiscardResult();
            sp.GetRequiredService<CiScheduler>().Advance(run.Id);
        }
    });

    private List<CiRun> WaitingRuns(Id<Pipeline> pipelineId, string commitSha) =>
        [.. runs.ListByPipeline(pipelineId).Where(r => r is { Status: RunStatus.Running, Building: true } && r.TestedSha == commitSha)];

    private static List<string> Validate(string manifestText)
    {
        if (!ManifestParser.TryParse(Encoding.UTF8.GetBytes(manifestText), out var manifest, out var parseIssues))
            return [.. parseIssues.Select(i => $"manifest {i}")];
        return [.. ManifestValidator.Validate(manifest).Errors.Select(i => i.ToString())];
    }

    private static List<string> Problems(IEnumerable<ResultProblem> problems) => [.. problems.Select(p => p.FormattedMessage)];

    private async Task DeleteJobAsync(Id<Pipeline> pipelineId, string commitSha)
    {
        try
        {
            await sp.GetRequiredService<ICiDefinitionBuilder>().DeleteAsync(pipelineId, commitSha);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the CI build Job of '{Sha}' in pipeline '{PipelineId}'", commitSha, pipelineId);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}

/// <summary>The config a CI run is pinned to: the pipeline's latest config snapshot, checked against the definition it runs.</summary>
public static class CiConfig
{
    /// <param name="definition">Names the definition in a problem: <c>version 7</c>, <c>this pull request's definition</c>.</param>
    public static (int? Snapshot, string? Config) Pin(
        Id<Pipeline> pipelineId, Manifest manifest, string definition, ConfigSnapshotService snapshots, List<string> problems)
    {
        if (manifest.Config.Count == 0)
            return (null, null);
        snapshots.TryGetLatest(pipelineId, out var snapshot);
        if (ConfigValidation.Effective(manifest, snapshots.Document(snapshot)).TryPickProblems(out var invalid, out var effective))
        {
            problems.Add($"Config {(snapshot is null ? "(none saved)" : $"#{snapshot.Number}")} does not satisfy {definition}: " +
                         string.Join("; ", invalid.Select(p => p.ToDisplayString())) + ".");
            return (snapshot?.Number, null);
        }

        return (snapshot?.Number, effective.ToJsonString());
    }
}
