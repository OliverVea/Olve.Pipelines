using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Triggers;

public class TriggerEvents
{
    public Event<EntityAdded<Trigger, Id<Trigger>>> OnAdded { get; } = new();
    public Event<EntityUpdated<Trigger, Id<Trigger>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<Trigger, Id<Trigger>>> OnDeleted { get; } = new();
}
