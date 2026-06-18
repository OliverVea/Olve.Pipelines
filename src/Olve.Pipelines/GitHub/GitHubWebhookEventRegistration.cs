using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// Bridges trigger lifecycle events to GitHub hook (de)registration. A trigger add for a
/// <see cref="GitHubWebhookTarget"/> enqueues a hook create; a trigger delete that had a registered
/// hook enqueues a hook delete. There is deliberately NO update path — reconcile changes a trigger
/// by delete+recreate (a new Id, secret, and delivery URL), so create/delete cover every case.
///
/// Handlers do no HTTP inline: they run synchronously during reconcile, so they only capture data
/// and post it to <see cref="GitHubHookWorkQueue"/>; the background drainer does the network calls.
/// </summary>
public class GitHubWebhookEventRegistration(
    TriggerEvents triggerEvents,
    EntityStore<Trigger> triggerStore,
    GitHubHookStateStore hookState,
    GitHubHookWorkQueue queue,
    WebhookOptions options,
    ILogger<GitHubWebhookEventRegistration> logger) : IRunOnStartup
{
    public Result Run()
    {
        triggerEvents.OnAdded.Subscribe(HandleTriggerAdded);
        triggerEvents.OnDeleted.Subscribe(HandleTriggerDeleted);
        return Result.Success();
    }

    private void HandleTriggerAdded(Id<Trigger> id)
    {
        if (!triggerStore.TryGet(id, out var trigger) || trigger.Target is not GitHubWebhookTarget target)
            return;

        if (string.IsNullOrEmpty(options.PublicBaseUrl))
        {
            logger.LogWarning(
                "GitHub webhook trigger '{TriggerId}' added but Webhooks:PublicBaseUrl is not configured; cannot auto-register the hook",
                id);
            return;
        }

        // Already have a hook for this trigger (e.g. state reloaded after a restart) — don't create a
        // duplicate. (Load-time adds don't reach here anyway: subscriptions wire after the load.)
        if (hookState.TryGet(id, out _))
            return;

        queue.Writer.TryWrite(new CreateHookWork(
            id, trigger.PipelineId, target.Owner, target.Repo, target.TokenSecretName, trigger.Secret));
    }

    private void HandleTriggerDeleted(Id<Trigger> id)
    {
        // Only triggers we actually registered a hook for have state. Absence => nothing to delete.
        if (!hookState.TryGet(id, out var state))
            return;

        queue.Writer.TryWrite(new DeleteHookWork(
            id, state.PipelineId, state.Owner, state.Repo, state.HookId, state.TokenSecretName));
    }
}
