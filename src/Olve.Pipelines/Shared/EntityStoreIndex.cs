using System.Collections.Immutable;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Shared;

/// <summary>
/// A secondary index over an <see cref="EntityStore{T}"/> that groups entity ids by a key.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety: each key is backed by an <see cref="ImmutableHashSet{T}"/>, and every write
/// funnels through a locked read-modify-write under <see cref="_gate"/>. <see cref="GetForKey"/>
/// returns the immutable set reference directly (no copy), so callers can enumerate it lock-free
/// and it will never mutate underneath them — even while a concurrent write adds or removes ids
/// for the same key. The caller decides whether to snapshot for stability across an
/// <c>await</c>; reads stay zero-copy.
/// </para>
/// <para>
/// The index keys on a value that never changes for a given entity (e.g. <c>PipelineId</c>), so it
/// deliberately does not subscribe to <see cref="EntityStore{T}.OnUpdated"/>. If the key could
/// change on update, this would be incorrect.
/// </para>
/// </remarks>
public sealed class EntityStoreIndex<T, TKey>
    where T : IHasId<Id<T>>
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, ImmutableHashSet<Id<T>>> _index = new();

    // Reverse map id -> key. The entity is already removed from the store by the time OnDeleted
    // fires, so the key cannot be recomputed from the entity at removal time; it is resolved here.
    private readonly Dictionary<Id<T>, TKey> _keyById = new();

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
        lock (_gate)
        {
            var current = _index.TryGetValue(key, out var ids) ? ids : ImmutableHashSet<Id<T>>.Empty;
            var next = current.Add(id);
            if (ReferenceEquals(next, current)) return; // already present

            _index[key] = next;
            _keyById[id] = key;
        }
    }

    private void Remove(Id<T> id)
    {
        lock (_gate)
        {
            if (!_keyById.Remove(id, out var key)) return;
            if (!_index.TryGetValue(key, out var current)) return;

            var next = current.Remove(id);
            if (next.IsEmpty) _index.Remove(key);
            else _index[key] = next;
        }
    }

    /// <summary>
    /// Returns the ids currently indexed under <paramref name="key"/>. The result is an immutable
    /// snapshot reference: it never mutates, so it is safe to enumerate without further locking.
    /// </summary>
    public IReadOnlyCollection<Id<T>> GetForKey(TKey key)
    {
        lock (_gate)
        {
            return _index.TryGetValue(key, out var ids) ? ids : ImmutableHashSet<Id<T>>.Empty;
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
