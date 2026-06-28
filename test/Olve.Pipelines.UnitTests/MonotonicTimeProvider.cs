namespace Olve.Pipelines.UnitTests;

/// <summary>
/// A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> advances by a fixed step on every
/// call. Gives each created entity a distinct, monotonically increasing <c>CreatedAt</c> so
/// latest-wins ordering is deterministic in tests (the Rocks <c>Make</c> mock returns a constant
/// timestamp, which would force every job into a CreatedAt tie). Thread-safe for concurrency tests.
/// </summary>
internal sealed class MonotonicTimeProvider(DateTimeOffset? start = null, TimeSpan? step = null) : TimeProvider
{
    private readonly long _step = (step ?? TimeSpan.FromMilliseconds(1)).Ticks;
    private long _ticks = (start ?? DateTimeOffset.UnixEpoch).UtcTicks;

    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Add(ref _ticks, _step), TimeSpan.Zero);
}
