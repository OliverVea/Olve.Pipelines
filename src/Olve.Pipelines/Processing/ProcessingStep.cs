using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Processing;

public enum ProcessingStepType { None, Script }

public record ProcessingStep(Id<ProcessingStep> Id, string Name, Id<Pipeline> PipelineId, ProcessingStepType Type = ProcessingStepType.None) : IHasId<Id<ProcessingStep>>;
