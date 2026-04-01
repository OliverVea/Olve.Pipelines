using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines;

public class PipelineEvents
{
    public Event<Id<Pipeline>> OnAdded { get; } = new();
    public Event<Id<Pipeline>> OnUpdated { get; } = new();
    public Event<Id<Pipeline>> OnDeleted { get; } = new();
}
