using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// The binding-derived deploy poll. For each bound pipeline it fetches the bound config
/// (config-before-build), then polls the branch head and advances
/// <see cref="PipelineConfigBinding.LastDeployedSha"/> when it moves.
///
/// Interim (v2 B1): the v1 YAML compile/apply and production run are gone. The config step only
/// verifies the fetch and records <see cref="NotImplementedProblem"/> on the binding status; the
/// deploy step logs the head advance. v2 reconcile (describe → version) and runs replace both.
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

    // Serializes the deploy decision (compare cursor → deploy → advance cursor). The
    // background loop, the binding webhook and reconcile-now can all poll the same binding at once;
    // without this, both saw the old cursor and each fired a deploy for one push (#39).
    // Held only around synchronous work — never across a network call or a reconcile drain.
    private readonly Lock _deployLock = new();

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

        // Config before build: if config can't be fetched, the deploy step is held off until it can.
        if (!await ReconcileConfigAsync(source, bindingService, binding, ct))
            return;

        await DeployAsync(source, bindingService, binding, ct);
    }

    /// <summary>Returns true if it is safe to proceed to the deploy step (config fetched).</summary>
    private async Task<bool> ReconcileConfigAsync(
        IConfigSource source, PipelineConfigBindingService bindingService,
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

        if (fetch is ConfigFetch.Changed changed)
        {
            _configEtags[binding.Id] = changed.ETag;
            logger.LogInformation("Fetched config {Sha} for '{Repo}@{Branch}' ({FileCount} files); v2 reconcile not implemented yet",
                changed.Sha, binding.Repo, binding.Branch, changed.Files.Count);
        }

        RecordNotImplemented(bindingService, CurrentOrSnapshot(bindingService, binding));
        return true;
    }

    /// <summary>The status recorded after a successful config fetch until v2 reconcile lands.</summary>
    public const string NotImplementedProblem = "v2 reconcile not implemented yet";

    // Only writes when the status differs, so steady state doesn't rewrite the snapshot every interval.
    private static void RecordNotImplemented(
        PipelineConfigBindingService bindingService, PipelineConfigBinding binding)
    {
        if (binding.Status is { Result: ReconcileResult.Error, Problems: [NotImplementedProblem] })
            return;
        bindingService.SetReconcileStatus(binding.Id, new ReconcileStatus(
            ReconcileResult.Error, DateTimeOffset.UtcNow, [NotImplementedProblem], binding.Status.DeclaredSecrets)).DiscardResult();
    }

    // The binding as currently stored, falling back to the caller's snapshot if it was unbound meanwhile.
    private static PipelineConfigBinding CurrentOrSnapshot(
        PipelineConfigBindingService bindingService, PipelineConfigBinding snapshot)
        => bindingService.TryGet(snapshot.Id).TryPickProblems(out _, out var current) ? snapshot : current;

    // Record an error outcome, carrying forward the last-known declared secrets so the badge still
    // lists them when a fetch/compile fails before a manifest is available.
    private static void RecordError(
        PipelineConfigBindingService bindingService, PipelineConfigBinding binding, IEnumerable<ResultProblem> problems)
        => bindingService.SetReconcileStatus(binding.Id, new ReconcileStatus(
            ReconcileResult.Error, DateTimeOffset.UtcNow,
            problems.Select(p => p.ToBriefString()).ToArray(),
            binding.Status.DeclaredSecrets)).DiscardResult();

    private async Task DeployAsync(
        IConfigSource source, PipelineConfigBindingService bindingService,
        PipelineConfigBinding binding, CancellationToken ct)
    {
        var headResult = await source.GetBranchHeadShaAsync(binding, ct);
        if (headResult.TryPickProblems(out var problems, out var head))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Deploy poll: could not read branch head for '{Repo}@{Branch}'",
                binding.Repo, binding.Branch);
            return;
        }

        lock (_deployLock)
        {
            // Re-read under the lock: `binding` is a snapshot from before the awaits above, so a
            // concurrent poll may already have fired this head and advanced the cursor.
            if (bindingService.TryGet(binding.Id).TryPickProblems(out _, out var current))
                return; // unbound meanwhile — nothing to deploy

            AdvanceIfMoved(bindingService, current, head);
        }
    }

    private void AdvanceIfMoved(
        PipelineConfigBindingService bindingService, PipelineConfigBinding binding, string head)
    {
        if (binding.LastDeployedSha == head)
            return;

        // First observation seeds the cursor — restarts and freshly-bound pipelines never treat
        // already-current code as a new deploy.
        if (binding.LastDeployedSha is null)
        {
            logger.LogInformation("Deploy poll: seeding cursor for '{Repo}@{Branch}' at {Sha}",
                binding.Repo, binding.Branch, head);
            bindingService.SetLastDeployedSha(binding.Id, head).DiscardResult(); // concurrent unbind is the only failure; moot
            return;
        }

        logger.LogInformation("Deploy poll: '{Repo}@{Branch}' advanced {Old} -> {New}; v2 runs not implemented yet, nothing triggered",
            binding.Repo, binding.Branch, binding.LastDeployedSha, head);
        bindingService.SetLastDeployedSha(binding.Id, head).DiscardResult(); // concurrent unbind is the only failure; moot
    }
}
