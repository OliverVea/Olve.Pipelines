using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// What we registered on GitHub for a given trigger, retained so the hook can later be deleted.
/// The trigger-delete event carries only an <see cref="Id{T}"/> (the entity is already gone), so
/// everything the GitHub <c>DELETE</c> needs — repo coordinates, the hook id, and the secret name
/// to resolve the PAT — must be held here.
/// </summary>
public record GitHubHookState(Id<Pipeline> PipelineId, string Owner, string Repo, long HookId, string TokenSecretName);

/// <summary>
/// Tracks the live GitHub hook registered per trigger, persisted to <c>github-hooks.json</c>.
///
/// Deliberately NOT an <see cref="Shared.AttachmentStore{TParent,TAttachment}"/>: an attachment
/// store auto-removes its entry when the parent trigger is deleted, which would destroy exactly the
/// repo/hook-id data the deletion handler needs to call GitHub. Entries are removed explicitly here,
/// only after the GitHub delete succeeds.
/// </summary>
public class GitHubHookStateStore
{
    private readonly ConcurrentDictionary<Id<Trigger>, GitHubHookState> _hooks = new();

    public Event<Id<Trigger>> OnSet { get; } = new();
    public Event<Id<Trigger>> OnRemoved { get; } = new();

    public void Set(Id<Trigger> id, GitHubHookState state)
    {
        _hooks[id] = state;
        OnSet.Invoke(id);
    }

    public bool TryGet(Id<Trigger> id, [NotNullWhen(true)] out GitHubHookState? state)
        => _hooks.TryGetValue(id, out state);

    public bool Remove(Id<Trigger> id)
    {
        if (!_hooks.TryRemove(id, out _)) return false;
        OnRemoved.Invoke(id);
        return true;
    }

    public IReadOnlyDictionary<Id<Trigger>, GitHubHookState> GetAll()
        => _hooks.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
