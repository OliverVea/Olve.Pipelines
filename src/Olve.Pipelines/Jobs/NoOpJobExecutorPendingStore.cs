using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

public class NoOpJobExecutorPendingStore
{
    private readonly ConcurrentDictionary<Id<Attempt>, TaskCompletionSource<NoOpJobResult>> _pending = new();

    public TaskCompletionSource<NoOpJobResult> Register(Id<Attempt> jobId)
    {
        var tcs = new TaskCompletionSource<NoOpJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[jobId] = tcs;
        return tcs;
    }

    public void Finish(Id<Attempt> jobId, NoOpJobResult result)
    {
        if (!_pending.TryRemove(jobId, out var tcs))
            throw new InvalidOperationException($"No pending execution for job '{jobId}'.");

        tcs.TrySetResult(result);
    }

    public bool HasPendingJob(Id<Attempt> jobId) => _pending.ContainsKey(jobId);

    public int PendingCount => _pending.Count;
}
