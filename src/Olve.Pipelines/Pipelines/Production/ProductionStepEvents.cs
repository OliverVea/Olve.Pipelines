using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public class ProductionStepEvents
{
    public Event<Id<ProductionStep>> OnAdded { get; } = new();
    public Event<Id<ProductionStep>> OnUpdated { get; } = new();
    public Event<Id<ProductionStep>> OnDeleted { get; } = new();
}
