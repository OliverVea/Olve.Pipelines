using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Drives GitHub hook (de)registration from binding lifecycle, so a binding that can have a push hook
/// has one and a deleted one does not. Subscribes directly to the binding
/// store (bindings have no domain event hub, same as the persistence service). Handlers run
/// synchronously during reconcile, so they only diff desired-vs-current and post work to
/// <see cref="BindingHookWorkQueue"/>; the drainer does the network calls.
///
/// A binding's repo never changes for a fixed Id (rebinding is delete+create with a new Id), so the
/// update handler only needs to react to credentials changes — not repo moves.
/// </summary>
public class BindingWebhookEventRegistration(
    EntityStore<PipelineConfigBinding> store,
    BindingHookStateStore hookState,
    BindingHookWorkQueue queue,
    WebhookOptions options,
    IdProvider idProvider,
    ILogger<BindingWebhookEventRegistration> logger) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(e => Reconcile(e.Id));
        store.OnUpdated.Subscribe(e => Reconcile(e.Id));
        store.OnDeleted.Subscribe(e => HandleDeleted(e.Id));
        return Result.Success();
    }

    /// <summary>Converges the registered hook to whether the binding can have one.</summary>
    private void Reconcile(Id<PipelineConfigBinding> id)
    {
        if (!store.TryGet(id, out var binding))
            return;

        // Bindings from before webhooks were always on have no HMAC key; the update re-enters here.
        if (string.IsNullOrEmpty(binding.WebhookSecret) && CanHaveHook(binding))
        {
            store.Set(binding with { WebhookSecret = idProvider.CreateSecret() });
            return;
        }

        var hasHook = hookState.TryGet(id, out var existing);
        var wantsHook = WantsHook(binding, out var owner, out var repo);

        if (wantsHook && (!hasHook || existing!.Url != BindingHookRegistrationService.BindingHookUrl(options, id)))
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

    /// <summary>A public base URL to receive deliveries and a credentials secret (the PAT that manages the hook).</summary>
    private bool CanHaveHook(PipelineConfigBinding binding)
        => !string.IsNullOrEmpty(options.PublicBaseUrl) && !string.IsNullOrEmpty(binding.CredentialsSecret);

    /// <summary>
    /// True when a webhook can be auto-registered: <see cref="CanHaveHook"/>, an HMAC secret exists, and
    /// the repo parses as <c>owner/name</c>. Polling carries the deploy either way.
    /// </summary>
    private bool WantsHook(PipelineConfigBinding binding, out string owner, out string repo)
    {
        owner = repo = string.Empty;

        if (!CanHaveHook(binding) || string.IsNullOrEmpty(binding.WebhookSecret))
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
