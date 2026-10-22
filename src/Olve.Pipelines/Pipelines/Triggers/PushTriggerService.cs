using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// Push detection for bound pipelines whose current version declares <c>onPush</c>: a
/// <see cref="PollScheduler"/> source per such binding (every <see cref="ReconcileOptions.PollInterval"/>),
/// nudged by the binding's GitHub webhook (<see cref="RequestCheck"/>). Each check runs through
/// <see cref="IConfigGate"/> (the config commit is applied first), then compares the branch head with
/// <see cref="PipelineConfigBinding.LastPushSha"/> and fires the push trigger when it advanced. The first
/// observation only seeds the cursor. Hosted only so shutdown cancels requested checks.
/// </summary>
public class PushTriggerService(
    EntityStore<PipelineConfigBinding> bindings,
    ReconcileOptions options,
    IServiceProvider sp,
    ILogger<PushTriggerService> logger) : IPollSourceProvider, IHostedService, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public IEnumerable<PollSource> Sources()
    {
        using var scope = sp.CreateScope();
        var inputs = scope.ServiceProvider.GetRequiredService<RunInputService>();
        return bindings.List()
            .Where(b => !inputs.GetTarget(b.PipelineId).TryPickProblems(out _, out var target) && target.Manifest.Triggers.OfType<PushTrigger>().Any())
            .Select(b => new PollSource($"push:{b.Id}", options.PollInterval, async ct =>
            {
                try
                {
                    await CheckAsync(b.PipelineId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Push check failed for '{Repo}@{Branch}' (pipeline '{PipelineId}')", b.Repo, b.Branch, b.PipelineId);
                }
            }))
            .ToList();
    }

    /// <summary>Applies the pipeline's config (<paramref name="forceConfig"/>: also retry a failed commit), then checks the branch head.</summary>
    public async Task CheckAsync(Id<Pipeline> pipelineId, CancellationToken ct, bool forceConfig = false)
    {
        using var scope = sp.CreateScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<IConfigGate>().RunIfConfigCurrentAsync(pipelineId, () => DetectAsync(services, pipelineId, ct), ct, forceConfig);
    }

    /// <summary>Starts <see cref="CheckAsync"/> in the background (a webhook delivery must not wait for a build).</summary>
    public Result RequestCheck(Id<Pipeline> pipelineId)
    {
        if (bindings.List().All(b => b.PipelineId != pipelineId))
            return new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId);

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(pipelineId, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Push check of pipeline '{PipelineId}' failed", pipelineId);
            }
        });
        return Result.Success();
    }

    private async Task DetectAsync(IServiceProvider services, Id<Pipeline> pipelineId, CancellationToken ct)
    {
        var bindingService = services.GetRequiredService<PipelineConfigBindingService>();
        if (bindingService.GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding))
            return;

        var headResult = await services.GetRequiredService<IConfigSource>().GetBranchHeadShaAsync(binding, ct);
        if (headResult.TryPickProblems(out var problems, out var head))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Push check: could not read branch head for '{Repo}@{Branch}'", binding.Repo, binding.Branch);
            return;
        }

        if (binding.LastPushSha == head)
            return;

        bindingService.SetLastPushSha(binding.Id, head).DiscardResult();

        // First observation seeds the cursor — restarts and freshly bound pipelines never treat
        // already-current code as a new push.
        if (binding.LastPushSha is null)
        {
            logger.LogInformation("Push check: seeding cursor for '{Repo}@{Branch}' at {Sha}", binding.Repo, binding.Branch, head);
            return;
        }

        logger.LogInformation("Push check: '{Repo}@{Branch}' advanced {Old} -> {New}; firing the push trigger",
            binding.Repo, binding.Branch, binding.LastPushSha, head);

        var fired = await services.GetRequiredService<IPushTriggerHandler>().FireAsync(binding, head, ct);
        if (fired.TryPickProblems(out var fireProblems))
            logger.LogProblems(LogLevel.Warning, fireProblems, "Push trigger of pipeline '{PipelineId}' for {Sha} did not start a run",
                binding.PipelineId, head);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _lifetime.CancelAsync();

    public void Dispose()
    {
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
