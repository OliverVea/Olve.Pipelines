using System.Collections.Concurrent;

namespace Olve.Pipelines.Jobs;

public class NoOpJobExecutorPendingStore
{
    private readonly ConcurrentDictionary<Id<Job>, TaskCompletionSource<NoOpJobResult>> _pending = new();

    public TaskCompletionSource<NoOpJobResult> Register(Id<Job> jobId)
    {
        var tcs = new TaskCompletionSource<NoOpJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[jobId] = tcs;
        return tcs;
    }

    public void Finish(Id<Job> jobId, NoOpJobResult result)
    {
        if (!_pending.TryRemove(jobId, out var tcs))
            throw new InvalidOperationException($"No pending execution for job '{jobId}'.");

        tcs.TrySetResult(result);
    }

    public bool HasPendingJob(Id<Job> jobId) => _pending.ContainsKey(jobId);

    public int PendingCount => _pending.Count;
}
