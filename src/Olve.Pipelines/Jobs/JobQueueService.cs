using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

public class JobQueueService(EntityStore<Attempt> store)
{
    public IEnumerable<Id<Attempt>> GetQueuedAttemptIds()
        => store.List()
            .Where(a => a.Status is Scheduled)
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Id);

    public IEnumerable<Attempt> GetActiveAttempts()
        => store.List()
            .Where(a => a.Status.IsActive())
            .OrderBy(a => a.CreatedAt);
}
