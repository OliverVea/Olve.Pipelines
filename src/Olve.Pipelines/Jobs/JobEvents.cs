using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Jobs;

public class JobEvents
{
    public Event<Id<Job>> OnAdded { get; } = new();
    public Event<Id<Job>> OnUpdated { get; } = new();
    public Event<Id<Job>> OnDeleted { get; } = new();
}
