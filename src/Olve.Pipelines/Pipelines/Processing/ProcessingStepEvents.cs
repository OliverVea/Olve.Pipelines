using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public class ProcessingStepEvents
{
    public Event<Id<ProcessingStep>> OnAdded { get; } = new();
    public Event<Id<ProcessingStep>> OnUpdated { get; } = new();
    public Event<Id<ProcessingStep>> OnDeleted { get; } = new();
}
