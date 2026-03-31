using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobQueueService(EntityStore<Job> store)
{
    private readonly List<Id<Job>> _queue = [];
    private readonly object _lock = new();

    public IReadOnlyList<Id<Job>> GetQueuedJobIds()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    public void HandleJobAdded(Id<Job> jobId)
    {
        if (!store.TryGet(jobId, out var job))
            return;

        if (job.Status is not Scheduled)
            return;

        lock (_lock)
        {
            _queue.Add(jobId);
        }
    }

    public void HandleJobUpdated(Id<Job> jobId)
    {
        if (!store.TryGet(jobId, out var job))
            return;

        if (job.Status is Scheduled)
            return;

        lock (_lock)
        {
            _queue.Remove(jobId);
        }
    }

    public void HandleJobDeleted(Id<Job> jobId)
    {
        lock (_lock)
        {
            _queue.Remove(jobId);
        }
    }
}
