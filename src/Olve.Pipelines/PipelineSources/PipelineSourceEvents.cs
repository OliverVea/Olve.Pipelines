using Olve.Pipelines.Shared;

namespace Olve.Pipelines.PipelineSources;

public class PipelineSourceEvents
{
    public Event<Id<PipelineSource>> OnAdded { get; } = new();
    public Event<Id<PipelineSource>> OnUpdated { get; } = new();
    public Event<Id<PipelineSource>> OnDeleted { get; } = new();
}
