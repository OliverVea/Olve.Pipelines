namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>
/// Leases hosts to tests. Shared leases spread over the hosts; an exclusive lease waits for its host to
/// drain while other tests move to other hosts, or wait when there is only one. After an exclusive lease
/// the host is reset; a host that can't be reset leaves the pool, and with none left every waiter fails.
/// </summary>
public sealed class ServerPool<T>(IReadOnlyList<T> hosts, Func<T, Task> reset, int sharedLimit) where T : class
{
    private sealed class Slot(T host)
    {
        public T Host { get; } = host;
        public int Shared { get; set; }
        public bool Exclusive { get; set; }
        public bool Draining { get; set; }
        public bool Clean { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = hosts.Select(h => new Slot(h)).ToList();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lost;

    public async Task<Lease> AcquireSharedAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ThrowIfEmpty();
                var slot = _slots
                    .Where(s => s is { Exclusive: false, Draining: false } && s.Shared < sharedLimit)
                    .MinBy(s => s.Shared);
                if (slot is not null)
                {
                    slot.Shared++;
                    slot.Clean = false;
                    return new Lease(this, slot.Host, Release: () => ReleaseSharedAsync(slot));
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct);
        }
    }

    /// <param name="fresh">The host must be freshly reset when the lease starts.</param>
    public async Task<Lease> AcquireExclusiveAsync(bool fresh = false, CancellationToken ct = default)
    {
        Slot slot;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ThrowIfEmpty();
                var free = _slots.Where(s => s is { Exclusive: false, Draining: false }).MinBy(s => s.Shared);
                if (free is not null)
                {
                    free.Draining = true;
                    Signal();
                    slot = free;
                    break;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct);
        }

        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (slot.Shared == 0)
                {
                    slot.Draining = false;
                    slot.Exclusive = true;
                    break;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct);
        }

        if (fresh && !slot.Clean)
            await ResetAsync(slot);

        bool lost;
        lock (_gate)
        {
            lost = !_slots.Contains(slot);
            slot.Clean = false;
        }
        return lost
            ? await AcquireExclusiveAsync(fresh, ct)
            : new Lease(this, slot.Host, Release: () => ReleaseExclusiveAsync(slot));
    }

    private Task ReleaseSharedAsync(Slot slot)
    {
        lock (_gate)
        {
            slot.Shared--;
            Signal();
        }
        return Task.CompletedTask;
    }

    private async Task ReleaseExclusiveAsync(Slot slot)
    {
        await ResetAsync(slot);
        lock (_gate)
        {
            slot.Exclusive = false;
            Signal();
        }
    }

    private async Task ResetAsync(Slot slot)
    {
        try
        {
            await reset(slot.Host);
            lock (_gate)
                slot.Clean = true;
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _slots.Remove(slot);
                _lost = $"Resetting host {slot.Host} failed, so it left the pool: {e.Message}";
                Signal();
            }
        }
    }

    private void ThrowIfEmpty()
    {
        if (_slots.Count == 0)
            throw new InvalidOperationException(_lost is null ? "The pool has no hosts." : $"No test host is left. {_lost}");
    }

    private void Signal()
    {
        _changed.TrySetResult();
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Lease(ServerPool<T> pool, T host, Func<Task> Release) : IAsyncDisposable
    {
        private int _released;

        public ServerPool<T> Pool { get; } = pool;
        public T Host { get; } = host;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                await Release();
        }
    }
}
