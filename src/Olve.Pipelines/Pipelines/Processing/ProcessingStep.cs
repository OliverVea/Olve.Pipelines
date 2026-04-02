using Olve.Pipelines.Pipelines;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Processing;

public record ProcessingStep(Id<ProcessingStep> Id, string Name, Id<Pipeline> PipelineId, int Order) : IHasId<Id<ProcessingStep>>;
