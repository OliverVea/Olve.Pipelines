using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public class ProcessingStepEvents
{
    public Event<EntityAdded<ProcessingStep, Id<ProcessingStep>>> OnAdded { get; } = new();
    public Event<EntityUpdated<ProcessingStep, Id<ProcessingStep>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<ProcessingStep, Id<ProcessingStep>>> OnDeleted { get; } = new();
}
