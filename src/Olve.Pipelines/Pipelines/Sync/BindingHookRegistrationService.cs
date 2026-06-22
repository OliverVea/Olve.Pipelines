using Olve.Pipelines.Configuration;
using Olve.Pipelines.GitHub;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Drains <see cref="BindingHookWorkQueue"/> and performs the GitHub hook API calls for binding
/// webhook-mode deploys, off the event-handler path. Mirrors the trigger drainer: PAT comes from the
/// binding's credentials secret (which must carry <c>admin:repo_hook</c>), create records the hook in
/// <see cref="BindingHookStateStore"/>, and delete removes the state entry only after GitHub confirms.
/// </summary>
public class BindingHookRegistrationService(
    BindingHookWorkQueue queue,
    IGitHubClient gitHub,
    BindingHookStateStore hookState,
    IPipelineSecretReader secretReader,
    WebhookOptions options,
    ILogger<BindingHookRegistrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Binding hook registration service started");

        await foreach (var work in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessAsync(work, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception processing binding hook work {WorkType}", work.GetType().Name);
            }
        }
    }

    /// <summary>Processes one work item. Exposed (not just driven by the loop) for unit testing.</summary>
    public async Task ProcessAsync(BindingHookWork work, CancellationToken ct)
    {
        switch (work)
        {
            case CreateBindingHookWork create:
                await CreateAsync(create, ct);
                break;
            case DeleteBindingHookWork delete:
                await DeleteAsync(delete, ct);
                break;
        }
    }

    private async Task CreateAsync(CreateBindingHookWork work, CancellationToken ct)
    {
        if (hookState.TryGet(work.BindingId, out _))
            return; // Already registered.

        var patResult = await secretReader.TryGetSecretAsync(work.PipelineId, work.CredentialsSecret, ct);
        if (patResult.TryPickProblems(out var patProblems, out var pat))
        {
            logger.LogProblems(LogLevel.Warning, patProblems, "Skipping binding hook create for '{BindingId}'", work.BindingId);
            return;
        }

        var url = $"{options.PublicBaseUrl!.TrimEnd('/')}/api/webhooks/binding/{work.BindingId}/github";
        var createResult = await gitHub.CreateHookAsync(
            work.Owner, work.Repo, pat, new GitHubHookConfig(url, work.HookSecret), ct);

        if (createResult.TryPickProblems(out var createProblems, out var hookId))
        {
            logger.LogProblems(LogLevel.Warning, createProblems, "Binding hook create failed for '{BindingId}'", work.BindingId);
            return;
        }

        hookState.Set(work.BindingId, new BindingHookState(
            work.PipelineId, work.Owner, work.Repo, hookId, work.CredentialsSecret));
    }

    private async Task DeleteAsync(DeleteBindingHookWork work, CancellationToken ct)
    {
        var patResult = await secretReader.TryGetSecretAsync(work.PipelineId, work.CredentialsSecret, ct);
        if (patResult.TryPickProblems(out var patProblems, out var pat))
        {
            logger.LogProblems(LogLevel.Warning, patProblems, "Cannot delete binding hook for '{BindingId}' (PAT unavailable)", work.BindingId);
            return;
        }

        var deleteResult = await gitHub.DeleteHookAsync(work.Owner, work.Repo, pat, work.HookId, ct);
        if (deleteResult.TryPickProblems(out var deleteProblems))
        {
            logger.LogProblems(LogLevel.Warning, deleteProblems, "Binding hook delete failed for '{BindingId}'", work.BindingId);
            return;
        }

        hookState.Remove(work.BindingId);
    }
}
