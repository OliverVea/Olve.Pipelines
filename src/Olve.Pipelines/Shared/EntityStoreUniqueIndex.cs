using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Shared;

/// <summary>
/// A secondary unique index over an <see cref="EntityStore{T}"/> mapping each key to a single id.
/// </summary>
/// <remarks>
/// Thread-safe: all reads and writes are guarded by <see cref="_gate"/>. A reverse id→key map is
/// kept so deletes can resolve the key (the entity is already gone from the store when
/// <see cref="EntityStore{T}.OnDeleted"/> fires). The index keys on a value that never changes for
/// a given entity, so it does not subscribe to <see cref="EntityStore{T}.OnUpdated"/>.
/// </remarks>
public sealed class EntityStoreUniqueIndex<T, TKey>
    where T : IHasId<Id<T>>
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, Id<T>> _index = new();
    private readonly Dictionary<Id<T>, TKey> _keyById = new();
    private readonly EntityStore<T> _store;
    private readonly Func<T, TKey> _keySelector;

    internal EntityStoreUniqueIndex(EntityStore<T> store, Func<T, TKey> keySelector)
    {
        _store = store;
        _keySelector = keySelector;

        foreach (var entity in store.List())
        {
            Add(entity.Id);
        }

        store.OnAdded.Subscribe(Add);
        store.OnDeleted.Subscribe(Remove);
    }

    private void Add(Id<T> id)
    {
        if (!_store.TryGet(id, out var entity)) return;

        var key = _keySelector(entity);
        lock (_gate)
        {
            _index[key] = id;
            _keyById[id] = key;
        }
    }

    private void Remove(Id<T> id)
    {
        lock (_gate)
        {
            if (!_keyById.Remove(id, out var key)) return;

            // Only drop the forward entry if it still points at this id; a later add under the same
            // key may have rebound it to a different id.
            if (_index.TryGetValue(key, out var current) && current.Equals(id))
                _index.Remove(key);
        }
    }

    public bool TryGet(TKey key, out Id<T> id)
    {
        lock (_gate)
        {
            return _index.TryGetValue(key, out id);
        }
    }

    public bool ContainsKey(TKey key)
    {
        lock (_gate)
        {
            return _index.ContainsKey(key);
        }
    }
}
