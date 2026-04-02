using Olve.Pipelines.Pipelines;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Production;

public record ProductionStep(Id<ProductionStep> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<ProductionStep>>;
