using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

/// <summary>Whose watcher it is: one per attempt, of any kind. All of them count against the Job slots.</summary>
public readonly record struct JobWatchKey(string Value)
{
    public static JobWatchKey Of(Id<Attempt> id) => new($"attempt/{id.Value.Value:N}");

    public static JobWatchKey Of(Id<Pipelines.Ci.CiAttempt> id) => new($"ci-attempt/{id.Value.Value:N}");

    public override string ToString() => Value;
}

public class JobWatcherRegistry(ILogger<JobWatcherRegistry> logger) : IHostedLifecycleService
{
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<JobWatchKey, Task> _watchers = new();

    public int ActiveCount => _watchers.Count;

    public bool IsRunning(Id<Attempt> id) => IsRunning(JobWatchKey.Of(id));

    public bool IsRunning(JobWatchKey key) => _watchers.ContainsKey(key);

    /// <summary>Completes once the watcher of <paramref name="id"/> has left the registry; at once if it has none.</summary>
    internal Task WhenStopped(Id<Attempt> id) => WhenStopped(JobWatchKey.Of(id));

    internal Task WhenStopped(JobWatchKey key) => _watchers.TryGetValue(key, out var watcher) ? watcher : Task.CompletedTask;

    public void EnsureRunning(Id<Attempt> id, Func<Task> work) => EnsureRunning(JobWatchKey.Of(id), work);

    public void EnsureRunning(JobWatchKey id, Func<Task> work)
    {
        // The watcher starts only once it is registered: removing itself in `finally` before
        // being stored would leave a finished watcher in the registry for good (and count it
        // against the concurrency limit).
        var registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = WatchAsync(id, registered.Task, work);
        registered.SetResult(_watchers.TryAdd(id, watcher));
    }

    private async Task WatchAsync(JobWatchKey key, Task<bool> registered, Func<Task> work)
    {
        if (!await registered)
            return;

        try
        {
            await work();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Watcher for job '{JobId}' threw unhandled exception", key.Value);
        }
        finally
        {
            _watchers.TryRemove(key, out _);
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        var watchers = _watchers.Values.ToArray();
        if (watchers.Length == 0) return;

        logger.LogInformation("Draining {Count} active job watcher(s) before shutdown", watchers.Length);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ShutdownDrainTimeout);

        try
        {
            await Task.WhenAll(watchers).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Job watcher drain timed out after {Timeout}", ShutdownDrainTimeout);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
