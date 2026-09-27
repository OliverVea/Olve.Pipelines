using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public class ProductionStepEvents
{
    public Event<EntityAdded<ProductionStep, Id<ProductionStep>>> OnAdded { get; } = new();
    public Event<EntityUpdated<ProductionStep, Id<ProductionStep>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<ProductionStep, Id<ProductionStep>>> OnDeleted { get; } = new();
}
