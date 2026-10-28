namespace Olve.Pipelines.Pipelines.Runs;

public class RunEvents
{
    public Event<EntityAdded<Run, Id<Run>>> OnAdded { get; } = new();
    public Event<EntityUpdated<Run, Id<Run>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<Run, Id<Run>>> OnDeleted { get; } = new();
}

public class AttemptEvents
{
    public Event<EntityAdded<Attempt, Id<Attempt>>> OnAdded { get; } = new();
    public Event<EntityUpdated<Attempt, Id<Attempt>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<Attempt, Id<Attempt>>> OnDeleted { get; } = new();
}
