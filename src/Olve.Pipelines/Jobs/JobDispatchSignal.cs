namespace Olve.Pipelines.Jobs;

/// <summary>Wakes <see cref="JobRunner"/> when an attempt is added or leaves an active status, so it dispatches without waiting for its tick. Singleton.</summary>
public sealed class JobDispatchSignal
{
    private TaskCompletionSource _next = New();

    public void Nudge() => Volatile.Read(ref _next).TrySetResult();

    /// <summary>Returns at the next nudge (or at once if one came since the last wait) or after <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var current = Volatile.Read(ref _next);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(current.Task, Task.Delay(timeout, cts.Token));
        await cts.CancelAsync();
        if (current.Task.IsCompleted)
            Interlocked.CompareExchange(ref _next, New(), current);
        ct.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
