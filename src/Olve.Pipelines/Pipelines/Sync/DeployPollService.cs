using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// The binding-derived deploy poll. For each bound pipeline it polls the bound repo's branch
/// head and fires a production run when the head advances past
/// <see cref="PipelineConfigBinding.LastDeployedSha"/>. This is the pull-based deploy mechanism
/// that replaces the manually-authored GitHub-commits poll trigger (<c>setup-pipeline</c> Option A).
///
/// Phase 3 is build-only: it just deploys on a new commit. Phase 4 prepends config reconcile to
/// this same loop (config-before-build) so a commit's config changes apply before its build runs.
/// </summary>
public class DeployPollService(
    EntityStore<PipelineConfigBinding> bindings,
    ReconcileOptions options,
    BindingHookStateStore bindingHookState,
    IServiceProvider sp,
    ILogger<DeployPollService> logger) : BackgroundService
{
    // Per-binding config ETag (in-memory). A pure rate-limit optimization: losing it on restart
    // costs one extra (idempotent) config fetch, never correctness. Concurrent because the
    // background poll loop and the reconcile-now endpoint can both touch it at once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Id<PipelineConfigBinding>, string> _configEtags = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Deploy poll service started");

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
            if (!ShouldPoll(binding))
                continue;

            try
            {
                await PollBindingAsync(binding, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Deploy poll failed for binding '{BindingId}' ({Repo}@{Branch})",
                    binding.Id, binding.Repo, binding.Branch);
            }
        }
    }

    /// <summary>
    /// Whether the background poll should run for this binding. Only the background loop honors the
    /// deploy-trigger mode; <see cref="ReconcileNowAsync"/> (explicit / just-bound) always runs.
    /// WebhookOnly suppresses polling only once a hook is confirmed registered — until then (or if it
    /// can't be registered at all) polling continues, so deploys never silently stop.
    /// </summary>
    private bool ShouldPoll(PipelineConfigBinding binding)
        => binding.DeployTrigger != BindingDeployTrigger.WebhookOnly
           || !bindingHookState.TryGet(binding.Id, out _);

    /// <summary>
    /// Runs one reconcile+deploy cycle for a single pipeline's binding immediately, off the poll
    /// schedule. Used by the reconcile-now endpoint so an operator (or a test) can apply a just-bound
    /// config without waiting up to <see cref="ReconcileOptions.PollInterval"/> for the next tick.
    /// Same code path as the background loop — no behavioural drift.
    /// </summary>
    public async Task<Result> ReconcileNowAsync(Id<Pipeline> pipelineId, CancellationToken ct)
    {
        var binding = bindings.List().FirstOrDefault(b => b.PipelineId == pipelineId);
        if (binding is null)
            return new ResultProblem($"Pipeline '{pipelineId}' has no config binding.");

        await PollBindingAsync(binding, ct);
        return Result.Success();
    }

    private async Task PollBindingAsync(PipelineConfigBinding binding, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IConfigSource>();
        var bindingService = scope.ServiceProvider.GetRequiredService<PipelineConfigBindingService>();

        // Config before build: a commit's config changes apply before its build runs. If config
        // can't be fetched/compiled/applied, the build is held off until it can.
        if (!await ReconcileConfigAsync(scope, source, bindingService, binding, ct))
            return;

        await DeployAsync(scope, source, bindingService, binding, ct);
    }

    /// <summary>Returns true if it is safe to proceed to the build (config is current/applied).</summary>
    private async Task<bool> ReconcileConfigAsync(
        IServiceScope scope, IConfigSource source, PipelineConfigBindingService bindingService,
        PipelineConfigBinding binding, CancellationToken ct)
    {
        _configEtags.TryGetValue(binding.Id, out var etag);

        var fetchResult = await source.FetchConfigAsync(binding, etag, ct);
        if (fetchResult.TryPickProblems(out var fetchProblems, out var fetch))
        {
            logger.LogProblems(LogLevel.Warning, fetchProblems, "Config fetch failed for '{Repo}@{Branch}'",
                binding.Repo, binding.Branch);
            RecordError(bindingService, binding, fetchProblems);
            return false;
        }

        if (fetch is not ConfigFetch.Changed changed)
        {
            ClearStaleError(bindingService, binding); // NotModified — config subtree unchanged
            return true;
        }

        _configEtags[binding.Id] = changed.ETag;

        if (changed.Sha == binding.LastSyncedSha)
        {
            ClearStaleError(bindingService, binding); // already applied this config
            return true;
        }

        var compiler = scope.ServiceProvider.GetRequiredService<ManifestCompiler>();
        if (compiler.Compile(changed.Files).TryPickProblems(out var compileProblems, out var manifest))
        {
            logger.LogProblems(LogLevel.Warning, compileProblems, "Config compile failed for '{Repo}' (cursor not advanced)",
                binding.Repo);
            RecordError(bindingService, binding, compileProblems);
            return false;
        }

        var coordinator = scope.ServiceProvider.GetRequiredService<ReconcileCoordinator>();
        if ((await coordinator.ReconcileAsync(binding.PipelineId, manifest, ct)).TryPickProblems(out var reconcileProblems))
        {
            logger.LogProblems(LogLevel.Warning, reconcileProblems, "Reconcile failed for '{Repo}' (cursor not advanced)",
                binding.Repo);
            RecordError(bindingService, binding, reconcileProblems);
            return false;
        }

        // Operational status/cursor writes on a binding just confirmed present; the only failure is a
        // concurrent unbind, which makes this poll moot — discard.
        bindingService.SetLastSyncedSha(binding.Id, changed.Sha).DiscardResult();
        bindingService.SetReconcileStatus(binding.Id, new ReconcileStatus(
            ReconcileResult.Success, DateTimeOffset.UtcNow, [], manifest.Secrets ?? [])).DiscardResult();
        logger.LogInformation("Reconciled '{Repo}@{Branch}' to config {Sha}", binding.Repo, binding.Branch, changed.Sha);
        return true;
    }

    // A healthy no-op poll (config unchanged or already applied) clears a stale error left by an
    // earlier failed poll — without it, a transient fetch failure (e.g. a missing token) sticks in
    // the badge until the next actual config change. Only writes when not already Success, so steady
    // state doesn't rewrite the snapshot every interval.
    private static void ClearStaleError(
        PipelineConfigBindingService bindingService, PipelineConfigBinding binding)
    {
        if (binding.Status.Result == ReconcileResult.Success)
            return;
        bindingService.SetReconcileStatus(binding.Id, new ReconcileStatus(
            ReconcileResult.Success, DateTimeOffset.UtcNow, [], binding.Status.DeclaredSecrets)).DiscardResult();
    }

    // Record an error outcome, carrying forward the last-known declared secrets so the badge still
    // lists them when a fetch/compile fails before a manifest is available.
    private static void RecordError(
        PipelineConfigBindingService bindingService, PipelineConfigBinding binding, IEnumerable<ResultProblem> problems)
        => bindingService.SetReconcileStatus(binding.Id, new ReconcileStatus(
            ReconcileResult.Error, DateTimeOffset.UtcNow,
            problems.Select(p => p.ToBriefString()).ToArray(),
            binding.Status.DeclaredSecrets)).DiscardResult();

    private async Task DeployAsync(
        IServiceScope scope, IConfigSource source, PipelineConfigBindingService bindingService,
        PipelineConfigBinding binding, CancellationToken ct)
    {
        var headResult = await source.GetBranchHeadShaAsync(binding, ct);
        if (headResult.TryPickProblems(out var problems, out var head))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Deploy poll: could not read branch head for '{Repo}@{Branch}'",
                binding.Repo, binding.Branch);
            return;
        }

        if (binding.LastDeployedSha == head)
            return;

        // First observation seeds the cursor without building — restarts and freshly-bound
        // pipelines don't trigger a surprise rebuild of already-current code. The initial deploy,
        // if wanted, is an explicit action at bind time.
        if (binding.LastDeployedSha is null)
        {
            logger.LogInformation("Deploy poll: seeding cursor for '{Repo}@{Branch}' at {Sha} (no initial build)",
                binding.Repo, binding.Branch, head);
            bindingService.SetLastDeployedSha(binding.Id, head).DiscardResult(); // concurrent unbind is the only failure; moot
            return;
        }

        logger.LogInformation("Deploy poll: '{Repo}@{Branch}' advanced {Old} -> {New}; firing production",
            binding.Repo, binding.Branch, binding.LastDeployedSha, head);

        var execution = scope.ServiceProvider.GetRequiredService<TriggerExecutionService>();
        if (execution.ExecuteProductionForPipeline(binding.PipelineId).TryPickProblems(out var execProblems))
        {
            // Leave the cursor unadvanced so the next interval retries this commit.
            logger.LogProblems(LogLevel.Warning, execProblems, "Deploy poll: production failed for pipeline '{PipelineId}'",
                binding.PipelineId);
            return;
        }

        bindingService.SetLastDeployedSha(binding.Id, head).DiscardResult(); // concurrent unbind is the only failure; moot
    }
}
