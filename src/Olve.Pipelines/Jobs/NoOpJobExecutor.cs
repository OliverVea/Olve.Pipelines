using System.Collections.Concurrent;
using Olve.Pipelines.Pipelines.Building;
using static Olve.Pipelines.Jobs.Job;

namespace Olve.Pipelines.Jobs;

public class NoOpJobExecutor(ILogger<NoOpJobExecutor> logger) : IJobExecutor
{
    private readonly ConcurrentDictionary<Id<Job>, TaskCompletionSource<JobExecutionResult>> _pending = new();

    public async Task<JobExecutionResult> ExecuteAsync(Job job, CancellationToken ct)
    {
        logger.LogInformation("No-op execution for job '{JobId}', awaiting Finish", job.Id);

        var tcs = new TaskCompletionSource<JobExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[job.Id] = tcs;

        await using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            return await tcs.Task;
        }
    }

    public void Finish(Id<Job> jobId, JobExecutionResult result)
    {
        if (!_pending.TryRemove(jobId, out var tcs))
            throw new InvalidOperationException($"No pending execution for job '{jobId}'.");

        tcs.TrySetResult(result);
    }

    public bool HasPendingJob(Id<Job> jobId) => _pending.ContainsKey(jobId);

    public int PendingCount => _pending.Count;
}
