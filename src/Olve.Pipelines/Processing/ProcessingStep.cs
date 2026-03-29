using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Processing;

public record ProcessingStep(Id<ProcessingStep> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<ProcessingStep>>;
