using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>Reconciles one config commit of a binding (seam over <see cref="PipelineReconcileService"/>).</summary>
public interface IConfigReconciler
{
    Task<Result<ReconcileReport>> ReconcileAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct);
}

/// <summary>Holds automatic run starts on a bound pipeline to config-before-run.</summary>
public interface IConfigLock
{
    /// <summary>
    /// Runs <paramref name="action"/> under the binding's lock once its current config commit is applied
    /// (reconciling it first if needed; <paramref name="force"/> also retries a commit whose reconcile
    /// failed). Returns false without running it when that fails. Unbound pipelines run it at once.
    /// </summary>
    Task<bool> RunIfConfigCurrentAsync(Id<Pipeline> pipelineId, Func<Task> action, CancellationToken ct, bool force = false);
}

/// <summary>Fires the pipeline's push trigger for a new branch head (seam over the trigger layer).</summary>
public interface IPushTriggerHandler
{
    Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct, RunOrigin? origin = null);
}

/// <summary>
/// Config sync of a binding: a conditional fetch of the config subtree's cursor (the newest commit
/// touching <see cref="PipelineConfigBinding.Path"/> or a local path dependency of its <c>package.json</c>);
/// when that commit is not yet applied, reconcile it (<see cref="IConfigReconciler"/>: build/describe →
/// validate → version → switch). A commit whose reconcile failed is not retried by the poll (only by a
/// forced reconcile or a restart). One <see cref="PollScheduler"/> source per binding, every
/// <see cref="ReconcileOptions.PollInterval"/>.
/// <para><b>Config-before-run.</b> Everything runs under a per-binding lock, and every automatic run start
/// of a bound pipeline (push, poll and webhook triggers) goes through <see cref="IConfigLock"/>: it starts
/// only once the current config commit is applied, so a run for a commit always follows that commit's
/// reconcile + switch, and no commit is built twice. While a version is pending (its config or secrets aren't
/// satisfied yet) the config is not current: automatic starts wait until it activates.</para>
/// </summary>
public class DeployPollService(
    EntityStore<PipelineConfigBinding> bindings,
    ReconcileOptions options,
    IServiceProvider sp,
    ILogger<DeployPollService> logger) : IPollSourceProvider, IConfigLock, IHostedService, IDisposable
{
    // Per-binding config cursor cache (in-memory). The ETag is a rate-limit optimization; the sha is
    // the cursor it stands for (a 304 means "still this sha"). Losing it on restart costs one fetch.
    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, (string ETag, string Sha)> _config = new();

    // The config commit whose reconcile last failed, per binding: not retried until forced.
    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, string> _failed = new();

    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, SemaphoreSlim> _locks = new();

    private readonly CancellationTokenSource _lifetime = new();

    public IEnumerable<PollSource> Sources() => bindings.List().Where(b => b.Polls).Select(binding => new PollSource(
        $"binding:{binding.Id}",
        options.PollInterval,
        async ct =>
        {
            try
            {
                await SyncConfigAsync(binding.Id, force: false, rebuild: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Config sync failed for binding '{BindingId}' ({Repo}@{Branch})",
                    binding.Id, binding.Repo, binding.Branch);
            }
        }));

    /// <summary>
    /// Syncs a pipeline's config now and waits for it. <paramref name="force"/> bypasses the ETag cache and
    /// retries a config commit whose reconcile failed; <paramref name="rebuild"/> reconciles the current commit
    /// again even when it is applied (for a server change in how a manifest is read).
    /// </summary>
    public async Task<Result> ReconcileNowAsync(Id<Pipeline> pipelineId, CancellationToken ct, bool force = true, bool rebuild = false)
    {
        var binding = bindings.List().FirstOrDefault(b => b.PipelineId == pipelineId);
        if (binding is null)
            return new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId);

        await SyncConfigAsync(binding.Id, force, rebuild, ct);
        return Result.Success();
    }

    /// <summary>
    /// Starts <see cref="ReconcileNowAsync"/> in the background and returns at once (webhook deliveries and
    /// HTTP callers must not wait for a build). Progress shows in the binding status.
    /// </summary>
    public Result RequestReconcile(Id<Pipeline> pipelineId, bool force, bool rebuild = false)
    {
        if (bindings.List().All(b => b.PipelineId != pipelineId))
            return new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId);

        _ = Task.Run(async () =>
        {
            try
            {
                (await ReconcileNowAsync(pipelineId, _lifetime.Token, force, rebuild)).DiscardResult();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background reconcile of pipeline '{PipelineId}' failed", pipelineId);
            }
        });
        return Result.Success();
    }

    private async Task SyncConfigAsync(Id<PipelineConfigBinding> bindingId, bool force, bool rebuild, CancellationToken ct)
    {
        var bindingLock = _locks.GetOrAdd(bindingId, _ => new SemaphoreSlim(1, 1));
        await bindingLock.WaitAsync(ct);
        try
        {
            using var scope = sp.CreateScope();
            var bindingService = scope.ServiceProvider.GetRequiredService<PipelineConfigBindingService>();
            if (bindingService.TryGet(bindingId).TryPickProblems(out _, out var binding))
                return; // unbound meanwhile

            await ReconcileConfigAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IConfigSource>(), bindingService, binding, force, rebuild, ct);
        }
        finally
        {
            bindingLock.Release();
        }
    }

    public async Task<bool> RunIfConfigCurrentAsync(Id<Pipeline> pipelineId, Func<Task> action, CancellationToken ct, bool force = false)
    {
        var bindingId = bindings.List().FirstOrDefault(b => b.PipelineId == pipelineId)?.Id;
        if (bindingId is null)
        {
            await action();
            return true;
        }

        var bindingLock = _locks.GetOrAdd(bindingId.Value, _ => new SemaphoreSlim(1, 1));
        await bindingLock.WaitAsync(ct);
        try
        {
            using var scope = sp.CreateScope();
            var bindingService = scope.ServiceProvider.GetRequiredService<PipelineConfigBindingService>();
            if (bindingService.TryGet(bindingId.Value).TryPickProblems(out _, out var binding))
                return false;
            if (!await ReconcileConfigAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IConfigSource>(), bindingService, binding, force, rebuild: false, ct))
                return false;

            await action();
            return true;
        }
        finally
        {
            bindingLock.Release();
        }
    }

    /// <summary>Returns true when the binding's current config commit is applied (safe to start runs).</summary>
    private async Task<bool> ReconcileConfigAsync(
        IServiceProvider services, IConfigSource source, PipelineConfigBindingService bindingService,
        PipelineConfigBinding binding, bool force, bool rebuild, CancellationToken ct)
    {
        _config.TryGetValue(binding.Id, out var cached);
        var etag = force ? null : cached.ETag;

        var fetchResult = await source.FetchConfigAsync(binding, etag, ct);
        if (fetchResult.TryPickProblems(out var fetchProblems, out var fetch))
        {
            logger.LogProblems(LogLevel.Warning, fetchProblems, "Config fetch failed for '{Repo}@{Branch}'", binding.Repo, binding.Branch);
            RecordStatus(bindingService, binding.Id, s => s with
            {
                Result = ReconcileResult.Error,
                LastSyncTime = DateTimeOffset.UtcNow,
                Problems = fetchProblems.Select(p => p.ToBriefString()).ToArray(),
            });
            return false;
        }

        string sha;
        switch (fetch)
        {
            case ConfigFetch.Changed changed:
                _config[binding.Id] = (changed.ETag, changed.Sha);
                sha = changed.Sha;
                break;
            case ConfigFetch.NotModified when cached.Sha is not null:
                sha = cached.Sha;
                break;
            default:
                return false;
        }

        if (binding.LastReconciledSha == sha && binding.Status.Result == ReconcileResult.Success && !rebuild)
            return !HasPending(services, binding.PipelineId);

        if (!force && _failed.TryGetValue(binding.Id, out var failedSha) && failedSha == sha)
            return false;

        logger.LogInformation("Reconciling config {Sha} of '{Repo}@{Branch}' ({Path}) for pipeline '{PipelineId}'",
            sha, binding.Repo, binding.Branch, binding.Path, binding.PipelineId);
        RecordStatus(bindingService, binding.Id, s => s with { InProgress = true, Sha = sha });

        Result<ReconcileReport> result;
        try
        {
            result = await services.GetRequiredService<IConfigReconciler>().ReconcileAsync(binding, sha, ct);
        }
        catch (OperationCanceledException)
        {
            RecordStatus(bindingService, binding.Id, s => s with { InProgress = false });
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reconcile of config {Sha} of '{Repo}@{Branch}' (pipeline '{PipelineId}') threw",
                sha, binding.Repo, binding.Branch, binding.PipelineId);
            result = Result.Failure<ReconcileReport>(new ResultProblem("Reconcile failed: {0}", ex.Message));
        }

        if (result.TryPickProblems(out var problems, out var report))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Reconcile of config {Sha} of '{Repo}@{Branch}' (pipeline '{PipelineId}') failed",
                sha, binding.Repo, binding.Branch, binding.PipelineId);
            _failed[binding.Id] = sha;
            RecordStatus(bindingService, binding.Id, s => s with
            {
                Result = ReconcileResult.Error,
                LastSyncTime = DateTimeOffset.UtcNow,
                Problems = problems.Select(p => p.ToBriefString()).ToArray(),
                Warnings = [],
                Sha = sha,
                InProgress = false,
            });
            return false;
        }

        _failed.TryRemove(binding.Id, out _);
        RecordStatus(bindingService, binding.Id, _ => new ReconcileStatus(
            ReconcileResult.Success, DateTimeOffset.UtcNow, [], report.Warnings, sha, InProgress: false, report.Version.Number));
        bindingService.SetLastReconciledSha(binding.Id, sha).DiscardResult();
        return report.Pending is null && !HasPending(services, binding.PipelineId);
    }

    private static bool HasPending(IServiceProvider services, Id<Pipeline> pipelineId)
        => services.GetRequiredService<ConfigActivationService>().TryGetPending(pipelineId, out _);

    private static void RecordStatus(
        PipelineConfigBindingService bindingService, Id<PipelineConfigBinding> id, Func<ReconcileStatus, ReconcileStatus> update)
    {
        if (bindingService.TryGet(id).TryPickProblems(out _, out var current))
            return;
        var next = update(current.Status);
        if (next != current.Status)
            bindingService.SetReconcileStatus(id, next).DiscardResult();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Cancels background reconciles started by <see cref="RequestReconcile"/>.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => _lifetime.CancelAsync();

    public void Dispose()
    {
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
