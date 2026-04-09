using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Shared;

public class AttachmentStore<TParent, TAttachment> where TParent : IHasId<Id<TParent>> where TAttachment : notnull
{
    private readonly ConcurrentDictionary<Id<TParent>, TAttachment> _attachments = new();

    public Event<Id<TParent>> OnSet { get; } = new();
    public Event<Id<TParent>> OnRemoved { get; } = new();

    public AttachmentStore(EntityStore<TParent> parentStore)
    {
        parentStore.OnDeleted.Subscribe(id => Remove(id));
    }

    public void Set(Id<TParent> id, TAttachment attachment)
    {
        _attachments[id] = attachment;
        OnSet.Invoke(id);
    }

    public bool TryGet(Id<TParent> id, [NotNullWhen(true)] out TAttachment? attachment)
        => _attachments.TryGetValue(id, out attachment);

    public bool Remove(Id<TParent> id)
    {
        if (!_attachments.TryRemove(id, out _)) return false;
        OnRemoved.Invoke(id);
        return true;
    }

    public IReadOnlyDictionary<Id<TParent>, TAttachment> GetAll() => _attachments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
