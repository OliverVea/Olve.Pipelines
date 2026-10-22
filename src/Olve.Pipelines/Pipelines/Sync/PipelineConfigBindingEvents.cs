namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingEvents
{
    public Event<EntityAdded<PipelineConfigBinding, Id<PipelineConfigBinding>>> OnAdded { get; } = new();
    public Event<EntityUpdated<PipelineConfigBinding, Id<PipelineConfigBinding>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<PipelineConfigBinding, Id<PipelineConfigBinding>>> OnDeleted { get; } = new();
}
