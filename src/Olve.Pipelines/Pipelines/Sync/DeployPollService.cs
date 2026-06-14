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
    IServiceProvider sp,
    ILogger<DeployPollService> logger) : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(60);

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

            await Task.Delay(LoopInterval, ct);
        }
    }

    private async Task PollCycleAsync(CancellationToken ct)
    {
        foreach (var binding in bindings.List())
        {
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

    private async Task PollBindingAsync(PipelineConfigBinding binding, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IConfigSource>();

        var headResult = await source.GetBranchHeadShaAsync(binding, ct);
        if (headResult.TryPickProblems(out var problems, out var head))
        {
            logger.LogWarning("Deploy poll: could not read branch head for '{Repo}@{Branch}': {Problems}",
                binding.Repo, binding.Branch, problems);
            return;
        }

        if (binding.LastDeployedSha == head)
            return;

        var bindingService = scope.ServiceProvider.GetRequiredService<PipelineConfigBindingService>();

        // First observation seeds the cursor without building — restarts and freshly-bound
        // pipelines don't trigger a surprise rebuild of already-current code. The initial deploy,
        // if wanted, is an explicit action at bind time.
        if (binding.LastDeployedSha is null)
        {
            logger.LogInformation("Deploy poll: seeding cursor for '{Repo}@{Branch}' at {Sha} (no initial build)",
                binding.Repo, binding.Branch, head);
            bindingService.SetLastDeployedSha(binding.Id, head);
            return;
        }

        logger.LogInformation("Deploy poll: '{Repo}@{Branch}' advanced {Old} -> {New}; firing production",
            binding.Repo, binding.Branch, binding.LastDeployedSha, head);

        var execution = scope.ServiceProvider.GetRequiredService<TriggerExecutionService>();
        if (execution.ExecuteProductionForPipeline(binding.PipelineId).TryPickProblems(out var execProblems))
        {
            // Leave the cursor unadvanced so the next interval retries this commit.
            logger.LogWarning("Deploy poll: production failed for pipeline '{PipelineId}': {Problems}",
                binding.PipelineId, execProblems);
            return;
        }

        bindingService.SetLastDeployedSha(binding.Id, head);
    }
}
