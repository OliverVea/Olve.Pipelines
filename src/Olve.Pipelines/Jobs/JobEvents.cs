using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

public class JobEvents
{
    public Event<Id<Job>> OnAdded { get; } = new();
    public Event<Id<Job>> OnUpdated { get; } = new();
    public Event<Id<Job>> OnDeleted { get; } = new();
}
