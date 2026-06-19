using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Drives GitHub hook (de)registration from binding lifecycle, so a binding in a webhook mode has a
/// live push hook and a poll-mode (or deleted) binding does not. Subscribes directly to the binding
/// store (bindings have no domain event hub, same as the persistence service). Handlers run
/// synchronously during reconcile, so they only diff desired-vs-current and post work to
/// <see cref="BindingHookWorkQueue"/>; the drainer does the network calls.
///
/// A binding's repo never changes for a fixed Id (rebinding is delete+create with a new Id), so the
/// update handler only needs to react to deploy-mode / credentials changes — not repo moves.
/// </summary>
public class BindingWebhookEventRegistration(
    EntityStore<PipelineConfigBinding> store,
    BindingHookStateStore hookState,
    BindingHookWorkQueue queue,
    WebhookOptions options,
    ILogger<BindingWebhookEventRegistration> logger) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(Reconcile);
        store.OnUpdated.Subscribe(Reconcile);
        store.OnDeleted.Subscribe(HandleDeleted);
        return Result.Success();
    }

    /// <summary>Converges the registered hook to what the binding's current mode wants.</summary>
    private void Reconcile(Id<PipelineConfigBinding> id)
    {
        if (!store.TryGet(id, out var binding))
            return;

        var hasHook = hookState.TryGet(id, out var existing);
        var wantsHook = WantsHook(binding, out var owner, out var repo);

        if (wantsHook && !hasHook)
        {
            queue.Writer.TryWrite(new CreateBindingHookWork(
                id, binding.PipelineId, owner, repo, binding.CredentialsSecret!, binding.WebhookSecret!));
        }
        else if (!wantsHook && hasHook)
        {
            queue.Writer.TryWrite(new DeleteBindingHookWork(
                id, existing!.PipelineId, existing.Owner, existing.Repo, existing.HookId, existing.CredentialsSecret));
        }
    }

    private void HandleDeleted(Id<PipelineConfigBinding> id)
    {
        if (!hookState.TryGet(id, out var existing))
            return;

        queue.Writer.TryWrite(new DeleteBindingHookWork(
            id, existing.PipelineId, existing.Owner, existing.Repo, existing.HookId, existing.CredentialsSecret));
    }

    /// <summary>
    /// True when the binding both wants a webhook (mode) and can have one auto-registered: a public
    /// base URL is set, a credentials secret is present (the PAT that manages the hook), an HMAC
    /// secret exists, and the repo parses as <c>owner/name</c>. Otherwise polling carries the deploy.
    /// </summary>
    private bool WantsHook(PipelineConfigBinding binding, out string owner, out string repo)
    {
        owner = repo = string.Empty;

        if (binding.DeployTrigger == BindingDeployTrigger.Poll)
            return false;
        if (string.IsNullOrEmpty(options.PublicBaseUrl) || string.IsNullOrEmpty(binding.CredentialsSecret) || string.IsNullOrEmpty(binding.WebhookSecret))
            return false;

        var parts = binding.Repo.Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            logger.LogWarning("Binding '{BindingId}' repo '{Repo}' is not 'owner/name'; cannot register a webhook", binding.Id, binding.Repo);
            return false;
        }

        owner = parts[0];
        repo = parts[1];
        return true;
    }
}
