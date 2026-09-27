using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public class PipelineEvents
{
    public Event<EntityAdded<Pipeline, Id<Pipeline>>> OnAdded { get; } = new();
    public Event<EntityUpdated<Pipeline, Id<Pipeline>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<Pipeline, Id<Pipeline>>> OnDeleted { get; } = new();
}
