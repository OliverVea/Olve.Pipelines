namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Waits until the run domain's changes so far are written to storage. An async boundary calls it before
/// anything outside the process depends on those changes: an API reply, a Job, the deletion of what a
/// restart would need. Concurrent waiters share one save.
/// </summary>
public interface IStateDurability
{
    /// <summary>Fails with an <see cref="UnavailableProblem"/> when the save failed or took too long. The change stays in memory and the timer keeps trying to save it.</summary>
    Task<Result> SavedAsync(CancellationToken ct = default);
}

public static class StateDurabilityExtensions
{
    /// <summary>A successful <paramref name="result"/> once it is saved; a failed one as it is.</summary>
    public static async Task<Result<T>> SavedAsync<T>(this IStateDurability durability, Result<T> result, CancellationToken ct = default)
    {
        if (result.TryPickProblems(out _, out _))
            return result;
        return (await durability.SavedAsync(ct)).TryPickProblems(out var problems) ? Result.Failure<T>(problems) : result;
    }
}

/// <summary>
/// Runs one <c>flush</c> at a time for all who asked since the last one started. A flush already running may
/// have read the state before a caller's change, so a caller always waits for a flush that starts after it asked.
/// </summary>
internal sealed class CoalescingFlush(Func<Task<bool>> flush)
{
    private readonly Lock _lock = new();
    private TaskCompletionSource<bool>? _next;
    private bool _running;

    public Task<bool> RequestAsync()
    {
        lock (_lock)
        {
            _next ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_running)
            {
                _running = true;
                _ = Task.Run(LoopAsync);
            }
            return _next.Task;
        }
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            TaskCompletionSource<bool>? waiters;
            lock (_lock)
            {
                waiters = _next;
                _next = null;
                if (waiters is null)
                {
                    _running = false;
                    return;
                }
            }

            bool saved;
            try
            {
                saved = await flush();
            }
            catch (Exception)
            {
                saved = false;
            }
            waiters.SetResult(saved);
        }
    }
}
