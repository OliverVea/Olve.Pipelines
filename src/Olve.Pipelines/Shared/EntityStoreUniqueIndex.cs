using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Shared;

public sealed class EntityStoreUniqueIndex<T, TKey>
    where T : IHasId<Id<T>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, Id<T>> _index = new();
    private readonly EntityStore<T> _store;
    private readonly Func<T, TKey> _keySelector;

    internal EntityStoreUniqueIndex(EntityStore<T> store, Func<T, TKey> keySelector)
    {
        _store = store;
        _keySelector = keySelector;

        foreach (var entity in store.List())
        {
            _index[keySelector(entity)] = entity.Id;
        }

        store.OnAdded += Add;
        store.OnDeleted += Remove;
    }

    private void Add(Id<T> id)
    {
        if (!_store.TryGet(id, out var entity)) return;
        _index[_keySelector(entity)] = id;
    }

    private void Remove(Id<T> id)
    {
        if (!_store.TryGet(id, out var entity)) return;
        _index.Remove(_keySelector(entity));
    }

    public bool TryGet(TKey key, out Id<T> id) => _index.TryGetValue(key, out id);

    public bool ContainsKey(TKey key) => _index.ContainsKey(key);
}
