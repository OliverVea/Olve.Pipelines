using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>Reconciles one config commit of a binding (seam over <see cref="PipelineReconcileService"/>).</summary>
public interface IConfigReconciler
{
    Task<Result<ReconcileReport>> ReconcileAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct);
}

/// <summary>Fires the pipeline's push trigger for a new branch head (seam over the trigger layer).</summary>
public interface IPushTriggerHandler
{
    Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct);
}

/// <summary>
/// The binding-derived poll, sequenced <b>config-before-run</b> per binding:
/// <list type="number">
/// <item><b>Config.</b> A conditional fetch of the config subtree's cursor (the newest commit touching
/// <see cref="PipelineConfigBinding.Path"/> or a local path dependency of its <c>package.json</c>). When that commit is not yet applied, reconcile it
/// (<see cref="IConfigReconciler"/>: build/describe → validate → version → switch). A commit whose
/// reconcile failed is not retried by the background loop (only by an explicit reconcile-now or a
/// restart), and while the current config commit is unapplied the run step is held.</item>
/// <item><b>Runs.</b> When the branch head advanced past <see cref="PipelineConfigBinding.LastPushSha"/>,
/// advance the cursor and fire the push trigger (<see cref="IPushTriggerHandler"/>). The first
/// observation only seeds the cursor.</item>
/// </list>
/// Both steps run under a per-binding async gate, so the background loop, the binding webhook and
/// reconcile-now never build or trigger the same commit twice, and a run for a commit always starts
/// after that commit's reconcile + switch.
/// </summary>
public class DeployPollService(
    EntityStore<PipelineConfigBinding> bindings,
    ReconcileOptions options,
    IServiceProvider sp,
    ILogger<DeployPollService> logger) : BackgroundService
{
    // Per-binding config cursor cache (in-memory). The ETag is a rate-limit optimization; the sha is
    // the cursor it stands for (a 304 means "still this sha"). Losing it on restart costs one fetch.
    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, (string ETag, string Sha)> _config = new();

    // The config commit whose reconcile last failed, per binding: not retried until forced.
    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, string> _failed = new();

    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, SemaphoreSlim> _gates = new();

    private readonly CancellationTokenSource _lifetime = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Deploy poll service started");
        await using var _ = ct.Register(_lifetime.Cancel);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in deploy poll cycle");
            }

            await Task.Delay(options.PollInterval, ct);
        }
    }

    private async Task PollCycleAsync(CancellationToken ct)
    {
        foreach (var binding in bindings.List())
        {
            try
            {
                await PollBindingAsync(binding.Id, force: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Deploy poll failed for binding '{BindingId}' ({Repo}@{Branch})",
                    binding.Id, binding.Repo, binding.Branch);
            }
        }
    }

    /// <summary>
    /// Runs one config + runs cycle for a pipeline's binding now and waits for it. <paramref name="force"/>
    /// bypasses the ETag cache and retries a config commit whose reconcile failed.
    /// </summary>
    public async Task<Result> ReconcileNowAsync(Id<Pipeline> pipelineId, CancellationToken ct, bool force = true)
    {
        var binding = bindings.List().FirstOrDefault(b => b.PipelineId == pipelineId);
        if (binding is null)
            return new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId);

        await PollBindingAsync(binding.Id, force, ct);
        return Result.Success();
    }

    /// <summary>
    /// Starts <see cref="ReconcileNowAsync"/> in the background and returns at once (webhook deliveries and
    /// HTTP callers must not wait for a build). Progress shows in the binding status.
    /// </summary>
    public Result RequestReconcile(Id<Pipeline> pipelineId, bool force)
    {
        if (bindings.List().All(b => b.PipelineId != pipelineId))
            return new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId);

        _ = Task.Run(async () =>
        {
            try
            {
                (await ReconcileNowAsync(pipelineId, _lifetime.Token, force)).DiscardResult();
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

    private async Task PollBindingAsync(Id<PipelineConfigBinding> bindingId, bool force, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(bindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var scope = sp.CreateScope();
            var source = scope.ServiceProvider.GetRequiredService<IConfigSource>();
            var bindingService = scope.ServiceProvider.GetRequiredService<PipelineConfigBindingService>();

            if (bindingService.TryGet(bindingId).TryPickProblems(out _, out var binding))
                return; // unbound meanwhile

            // Config before runs: while the current config commit is not applied, no run starts.
            if (!await ReconcileConfigAsync(scope.ServiceProvider, source, bindingService, binding, force, ct))
                return;

            await DeployAsync(scope.ServiceProvider, source, bindingService, bindingId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Returns true when the binding's current config commit is applied (safe to start runs).</summary>
    private async Task<bool> ReconcileConfigAsync(
        IServiceProvider services, IConfigSource source, PipelineConfigBindingService bindingService,
        PipelineConfigBinding binding, bool force, CancellationToken ct)
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

        if (binding.LastReconciledSha == sha && binding.Status.Result == ReconcileResult.Success)
            return true;

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = Result.Failure<ReconcileReport>(new ResultProblem("Reconcile failed: {0}", ex.Message));
        }

        if (result.TryPickProblems(out var problems, out var report))
        {
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
        return true;
    }

    private static void RecordStatus(
        PipelineConfigBindingService bindingService, Id<PipelineConfigBinding> id, Func<ReconcileStatus, ReconcileStatus> update)
    {
        if (bindingService.TryGet(id).TryPickProblems(out _, out var current))
            return;
        var next = update(current.Status);
        if (next != current.Status)
            bindingService.SetReconcileStatus(id, next).DiscardResult();
    }

    private async Task DeployAsync(
        IServiceProvider services, IConfigSource source, PipelineConfigBindingService bindingService,
        Id<PipelineConfigBinding> bindingId, CancellationToken ct)
    {
        if (bindingService.TryGet(bindingId).TryPickProblems(out _, out var binding))
            return;

        var headResult = await source.GetBranchHeadShaAsync(binding, ct);
        if (headResult.TryPickProblems(out var problems, out var head))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Deploy poll: could not read branch head for '{Repo}@{Branch}'",
                binding.Repo, binding.Branch);
            return;
        }

        if (binding.LastPushSha == head)
            return;

        bindingService.SetLastPushSha(binding.Id, head).DiscardResult();

        // First observation seeds the cursor — restarts and freshly bound pipelines never treat
        // already-current code as a new push.
        if (binding.LastPushSha is null)
        {
            logger.LogInformation("Deploy poll: seeding cursor for '{Repo}@{Branch}' at {Sha}", binding.Repo, binding.Branch, head);
            return;
        }

        logger.LogInformation("Deploy poll: '{Repo}@{Branch}' advanced {Old} -> {New}; firing the push trigger",
            binding.Repo, binding.Branch, binding.LastPushSha, head);

        var fired = await services.GetRequiredService<IPushTriggerHandler>().FireAsync(binding, head, ct);
        if (fired.TryPickProblems(out var fireProblems))
            logger.LogProblems(LogLevel.Warning, fireProblems, "Push trigger of pipeline '{PipelineId}' for {Sha} did not start a run",
                binding.PipelineId, head);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose(); // first: cancelling the stopping token runs a callback that cancels _lifetime
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
