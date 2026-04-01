using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Olve.Results;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Shared;

public class EntityStore<T> where T : IHasId<Id<T>>
{
    private readonly ConcurrentDictionary<Id<T>, T> _entities;

    public EntityStore(IEnumerable<T> initialEntities)
    {
        _entities = new(initialEntities.Select(e => new KeyValuePair<Id<T>, T>(e.Id, e)));
    }

    public Event<Id<T>> OnAdded { get; } = new();
    public Event<Id<T>> OnUpdated { get; } = new();
    public Event<Id<T>> OnDeleted { get; } = new();

    public void Set(T entity)
    {
        var isUpdate = _entities.ContainsKey(entity.Id);
        _entities[entity.Id] = entity;

        if (isUpdate)
            OnUpdated.Invoke(entity.Id);
        else
            OnAdded.Invoke(entity.Id);
    }

    public bool TryGet(Id<T> id, [NotNullWhen(true)] out T? entity) => _entities.TryGetValue(id, out entity);

    public IReadOnlyList<T> List() => _entities.Values.ToList();

    public DeletionResult Delete(Id<T> id)
    {
        if (!_entities.TryRemove(id, out _))
            return DeletionResult.NotFound();

        OnDeleted.Invoke(id);
        return DeletionResult.Success();
    }

    public bool Contains(Id<T> id) => _entities.ContainsKey(id);

    public EntityStoreIndex<T, TKey> CreateIndex<TKey>(Func<T, TKey> keySelector) where TKey : notnull
        => new(this, keySelector);

    public EntityStoreUniqueIndex<T, TKey> CreateUniqueIndex<TKey>(Func<T, TKey> keySelector) where TKey : notnull
        => new(this, keySelector);
}
