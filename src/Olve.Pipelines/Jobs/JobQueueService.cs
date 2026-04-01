using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.Jobs;

public class JobQueueService(EntityStore<Job> store)
{
    public IEnumerable<Id<Job>> GetQueuedJobIds()
        => store.List()
            .Where(j => j.Status is Scheduled)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id);
}
