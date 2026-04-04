using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

public class JobGroupEvents
{
    public Event<Id<JobGroup>> OnCompleted { get; } = new();
    public Event<Id<JobGroup>> OnFailed { get; } = new();
}
