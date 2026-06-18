using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// Drains <see cref="GitHubHookWorkQueue"/> and performs the actual GitHub API calls off the
/// event-handler path. Resolves each pipeline's PAT from its K8s secret at call time. On create it
/// records the resulting hook in <see cref="GitHubHookStateStore"/>; on delete it removes the state
/// entry only after GitHub confirms the deletion, so a failed delete keeps the hook id for a retry.
/// </summary>
public class GitHubHookRegistrationService(
    GitHubHookWorkQueue queue,
    IGitHubClient gitHub,
    GitHubHookStateStore hookState,
    IPipelineSecretReader secretReader,
    WebhookOptions options,
    ILogger<GitHubHookRegistrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("GitHub hook registration service started");

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
                logger.LogError(ex, "Unhandled exception processing GitHub hook work {WorkType}", work.GetType().Name);
            }
        }
    }

    /// <summary>Processes one work item. Exposed (not just driven by the loop) for unit testing.</summary>
    public async Task ProcessAsync(GitHubHookWork work, CancellationToken ct)
    {
        switch (work)
        {
            case CreateHookWork create:
                await CreateAsync(create, ct);
                break;
            case DeleteHookWork delete:
                await DeleteAsync(delete, ct);
                break;
        }
    }

    private async Task CreateAsync(CreateHookWork work, CancellationToken ct)
    {
        if (hookState.TryGet(work.TriggerId, out _))
            return; // Already registered (e.g. duplicate event); nothing to do.

        var patResult = await secretReader.TryGetSecretAsync(work.PipelineId, work.TokenSecretName, ct);
        if (patResult.TryPickProblems(out var patProblems, out var pat))
        {
            logger.LogWarning("Skipping GitHub hook create for trigger '{TriggerId}': {Problems}", work.TriggerId, patProblems);
            return;
        }

        var url = $"{options.PublicBaseUrl!.TrimEnd('/')}/api/webhooks/github/{work.TriggerId}";
        var createResult = await gitHub.CreateHookAsync(
            work.Owner, work.Repo, pat, new GitHubHookConfig(url, work.HookSecret), ct);

        if (createResult.TryPickProblems(out var createProblems, out var hookId))
        {
            logger.LogWarning("GitHub hook create failed for trigger '{TriggerId}': {Problems}", work.TriggerId, createProblems);
            return;
        }

        hookState.Set(work.TriggerId, new GitHubHookState(
            work.PipelineId, work.Owner, work.Repo, hookId, work.TokenSecretName));
    }

    private async Task DeleteAsync(DeleteHookWork work, CancellationToken ct)
    {
        var patResult = await secretReader.TryGetSecretAsync(work.PipelineId, work.TokenSecretName, ct);
        if (patResult.TryPickProblems(out var patProblems, out var pat))
        {
            // Keep the state entry so the hook id is not lost; a future delete attempt can retry.
            logger.LogWarning("Cannot delete GitHub hook for trigger '{TriggerId}' (PAT unavailable): {Problems}", work.TriggerId, patProblems);
            return;
        }

        var deleteResult = await gitHub.DeleteHookAsync(work.Owner, work.Repo, pat, work.HookId, ct);
        if (deleteResult.TryPickProblems(out var deleteProblems))
        {
            logger.LogWarning("GitHub hook delete failed for trigger '{TriggerId}': {Problems}", work.TriggerId, deleteProblems);
            return;
        }

        hookState.Remove(work.TriggerId);
    }
}
