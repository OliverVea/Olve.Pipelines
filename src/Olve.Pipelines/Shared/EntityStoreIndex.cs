using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Shared;

public sealed class EntityStoreIndex<T, TKey>
    where T : IHasId<Id<T>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, HashSet<Id<T>>> _index = new();
    private readonly EntityStore<T> _store;
    private readonly Func<T, TKey> _keySelector;

    internal EntityStoreIndex(EntityStore<T> store, Func<T, TKey> keySelector)
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
        if (!_index.TryGetValue(key, out var ids))
        {
            ids = [];
            _index[key] = ids;
        }

        ids.Add(id);
    }

    private void Remove(Id<T> id)
    {
        if (!_store.TryGet(id, out var entity)) return;

        var key = _keySelector(entity);
        if (!_index.TryGetValue(key, out var ids)) return;

        ids.Remove(id);
        if (ids.Count == 0) _index.Remove(key);
    }

    public IReadOnlyCollection<Id<T>> GetForKey(TKey key)
        => _index.TryGetValue(key, out var ids) ? ids : [];

    public bool ContainsKey(TKey key) => _index.ContainsKey(key);
}
