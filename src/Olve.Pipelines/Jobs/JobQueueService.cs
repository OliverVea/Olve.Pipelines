using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobQueueService
{
    private readonly EntityStore<Job> _store;
    private readonly List<Id<Job>> _queue = [];
    private readonly object _lock = new();

    public JobQueueService(EntityStore<Job> store)
    {
        _store = store;

        store.OnAdded += OnJobAdded;
        store.OnUpdated += OnJobUpdated;
        store.OnDeleted += OnJobDeleted;
    }

    public IReadOnlyList<Id<Job>> GetQueuedJobIds()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    private void OnJobAdded(Id<Job> jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            return;

        if (job.Status is not Scheduled)
            return;

        lock (_lock)
        {
            _queue.Add(jobId);
        }
    }

    private void OnJobUpdated(Id<Job> jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            return;

        if (job.Status is Scheduled)
            return;

        lock (_lock)
        {
            _queue.Remove(jobId);
        }
    }

    private void OnJobDeleted(Id<Job> jobId)
    {
        lock (_lock)
        {
            _queue.Remove(jobId);
        }
    }
}
