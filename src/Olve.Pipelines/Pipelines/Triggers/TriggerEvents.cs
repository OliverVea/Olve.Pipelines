using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Triggers;

public class TriggerEvents
{
    public Event<Id<Trigger>> OnAdded { get; } = new();
    public Event<Id<Trigger>> OnUpdated { get; } = new();
    public Event<Id<Trigger>> OnDeleted { get; } = new();
}
