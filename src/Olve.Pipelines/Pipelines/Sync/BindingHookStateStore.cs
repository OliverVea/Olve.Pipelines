using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// The GitHub hook registered for a binding's webhook-mode deploy, retained so it can be deleted
/// later. As with the trigger hook store, the delete event carries only an id and everything the
/// GitHub <c>DELETE</c> needs (repo coordinates, hook id, the credentials-secret name to resolve the
/// PAT) must live here. A plain singleton — not an AttachmentStore — so entries are removed only
/// after a confirmed GitHub delete.
/// </summary>
public record BindingHookState(
    Id<Pipeline> PipelineId, string Owner, string Repo, long HookId, string CredentialsSecret);

public class BindingHookStateStore
{
    private readonly ConcurrentDictionary<Id<PipelineConfigBinding>, BindingHookState> _hooks = new();

    public Event<Id<PipelineConfigBinding>> OnSet { get; } = new();
    public Event<Id<PipelineConfigBinding>> OnRemoved { get; } = new();

    public void Set(Id<PipelineConfigBinding> id, BindingHookState state)
    {
        _hooks[id] = state;
        OnSet.Invoke(id);
    }

    public bool TryGet(Id<PipelineConfigBinding> id, [NotNullWhen(true)] out BindingHookState? state)
        => _hooks.TryGetValue(id, out state);

    public bool Remove(Id<PipelineConfigBinding> id)
    {
        if (!_hooks.TryRemove(id, out _)) return false;
        OnRemoved.Invoke(id);
        return true;
    }

    public IReadOnlyDictionary<Id<PipelineConfigBinding>, BindingHookState> GetAll()
        => _hooks.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
