using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

public class NoOpJobExecutorPendingStore
{
    private readonly ConcurrentDictionary<Id<Attempt>, TaskCompletionSource<NoOpJobResult>> _pending = new();
    private readonly ConcurrentDictionary<Id<Attempt>, TaskCompletionSource> _registered = new();

    public TaskCompletionSource<NoOpJobResult> Register(Id<Attempt> jobId)
    {
        var tcs = new TaskCompletionSource<NoOpJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[jobId] = tcs;
        Registered(jobId).TrySetResult();
        return tcs;
    }

    public void Finish(Id<Attempt> jobId, NoOpJobResult result)
    {
        if (!_pending.TryRemove(jobId, out var tcs))
            throw new InvalidOperationException($"No pending execution for job '{jobId}'.");

        _registered.TryRemove(jobId, out _);
        tcs.TrySetResult(result);
    }

    public bool HasPendingJob(Id<Attempt> jobId) => _pending.ContainsKey(jobId);

    /// <summary>Completes once <paramref name="jobId"/> awaits <see cref="Finish"/>.</summary>
    public Task WhenPending(Id<Attempt> jobId) => Registered(jobId).Task;

    public int PendingCount => _pending.Count;

    private TaskCompletionSource Registered(Id<Attempt> jobId)
        => _registered.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
