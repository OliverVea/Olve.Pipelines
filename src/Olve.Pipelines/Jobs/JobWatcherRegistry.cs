using System.Collections.Concurrent;

namespace Olve.Pipelines.Jobs;

public class JobWatcherRegistry(ILogger<JobWatcherRegistry> logger) : IHostedLifecycleService
{
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Id<Job>, Task> _watchers = new();

    public int ActiveCount => _watchers.Count;

    public bool IsRunning(Id<Job> id) => _watchers.ContainsKey(id);

    public void EnsureRunning(Id<Job> id, Func<Task> work)
    {
        _watchers.GetOrAdd(id, key => Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watcher for job '{JobId}' threw unhandled exception", key);
            }
            finally
            {
                _watchers.TryRemove(key, out _);
            }
        }));
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
