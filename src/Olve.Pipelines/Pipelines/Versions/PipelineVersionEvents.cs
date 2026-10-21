namespace Olve.Pipelines.Pipelines.Versions;

public class PipelineVersionEvents
{
    public Event<EntityAdded<PipelineVersion, Id<PipelineVersion>>> OnAdded { get; } = new();
    public Event<EntityUpdated<PipelineVersion, Id<PipelineVersion>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<PipelineVersion, Id<PipelineVersion>>> OnDeleted { get; } = new();
}
