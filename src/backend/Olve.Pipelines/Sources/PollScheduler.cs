using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Sources;

/// <summary>Something checked on an interval. <see cref="Key"/> identifies it across ticks; a new key is due at once.</summary>
public sealed record PollSource(string Key, TimeSpan Interval, Func<CancellationToken, Task> CheckAsync);

/// <summary>Lists the sources to poll right now; asked every tick, so sources come and go with their owner.</summary>
public interface IPollSourceProvider
{
    IEnumerable<PollSource> Sources();
}

/// <summary>
/// The polling primitive: one loop over every <see cref="IPollSourceProvider"/>. Each due source is checked
/// in the background; a source still running is not started again, and one failing source never stops the
/// others. Starts once the host has started (persistence change tracking is subscribed) and state is loaded.
/// </summary>
public class PollScheduler(
    IEnumerable<IPollSourceProvider> providers,
    IPersistenceReadiness readiness,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<PollScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan MaxTick = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, DateTimeOffset> _nextAt = [];
    private readonly Dictionary<string, Task> _running = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await WaitForStartAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogInformation("Poll scheduler started with {ProviderCount} source providers", providers.Count());
        while (!ct.IsCancellationRequested)
        {
            var tick = MaxTick;
            try
            {
                tick = Tick(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in the poll scheduler");
            }

            try
            {
                await Task.Delay(tick, timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task WaitForStartAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource();
        await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (ct.Register(() => started.TrySetCanceled(ct)))
            await started.Task;

        while (!readiness.IsReady)
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, ct);
    }

    /// <summary>Starts every due source; returns how long to wait before the next tick.</summary>
    internal TimeSpan Tick(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var tick = MaxTick;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in providers.SelectMany(ListSources))
        {
            seen.Add(source.Key);
            if (source.Interval < tick)
                tick = source.Interval;

            if (_running.TryGetValue(source.Key, out var running) && !running.IsCompleted)
                continue;
            if (_nextAt.TryGetValue(source.Key, out var nextAt) && now < nextAt)
                continue;

            _nextAt[source.Key] = now + source.Interval;
            _running[source.Key] = RunAsync(source, ct);
        }

        foreach (var key in _nextAt.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _nextAt.Remove(key);
            _running.Remove(key);
        }

        return tick > TimeSpan.Zero ? tick : MaxTick;
    }

    internal Task WhenIdle() => Task.WhenAll(_running.Values);

    private IEnumerable<PollSource> ListSources(IPollSourceProvider provider)
    {
        try
        {
            return provider.Sources().ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Poll source provider '{Provider}' failed to list its sources", provider.GetType().Name);
            return [];
        }
    }

    private async Task RunAsync(PollSource source, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => source.CheckAsync(ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Poll source '{SourceKey}' failed", source.Key);
        }
    }
}
