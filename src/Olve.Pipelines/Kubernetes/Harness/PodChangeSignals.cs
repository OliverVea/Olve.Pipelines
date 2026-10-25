namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Wakes a Job's waiter when its pod changes (fed by <see cref="PodWatchService"/>), so a finished step is
/// collected at once instead of on the next poll. Singleton.
/// </summary>
public sealed class PodChangeSignals(TimeProvider timeProvider)
{
    private sealed class Waiter
    {
        public readonly TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
    }

    private readonly Dictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void Notify(string jobName)
    {
        Waiter? waiter;
        lock (_lock)
        {
            if (_waiters.Remove(jobName, out waiter) is false)
                return;
        }
        waiter.Changed.TrySetResult();
    }

    /// <summary>Registers interest in the Job's next pod change; arm before reading the pod's state so no change is missed.
    /// Every <see cref="Arm"/> must be followed by one <see cref="WaitAsync"/>.</summary>
    public Task Arm(string jobName)
    {
        lock (_lock)
        {
            if (!_waiters.TryGetValue(jobName, out var waiter))
                _waiters[jobName] = waiter = new Waiter();
            waiter.Count++;
            return waiter.Changed.Task;
        }
    }

    /// <summary>Returns when <paramref name="armed"/> fires or after <paramref name="timeout"/>, whichever is first.</summary>
    public async Task WaitAsync(string jobName, Task armed, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Task.WhenAny(armed, Task.Delay(timeout, timeProvider, cts.Token));
        }
        finally
        {
            await cts.CancelAsync();
            Release(jobName, armed);
        }
        ct.ThrowIfCancellationRequested();
    }

    private void Release(string jobName, Task armed)
    {
        lock (_lock)
        {
            if (_waiters.TryGetValue(jobName, out var waiter) && waiter.Changed.Task == armed && --waiter.Count == 0)
                _waiters.Remove(jobName);
        }
    }
}

/// <summary>Watches the runner namespace's harness pods and signals <see cref="PodChangeSignals"/>; reconnects on any end or error.</summary>
public sealed class PodWatchService(
    IHarnessKubernetesClient kubernetes,
    KubernetesOptions options,
    PodChangeSignals signals,
    ILogger<PodWatchService> logger) : BackgroundService
{
    private const string JobNameLabel = "batch.kubernetes.io/job-name";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var pod in kubernetes.WatchPodsAsync(options.Namespace, "app=olve-pipelines", stoppingToken))
                {
                    failures = 0;
                    if (pod.Metadata.Labels?.GetValueOrDefault(JobNameLabel) is { } job)
                        signals.Notify(job);
                }
                // A watch that ends at once must not spin.
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Polling still collects every Job; only the first failure in a row is worth a warning.
                if (failures++ == 0)
                    logger.LogWarning(ex, "Pod watch in namespace {Namespace} failed; steps are collected by polling until it reconnects", options.Namespace);
                await Task.Delay(RetryDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
