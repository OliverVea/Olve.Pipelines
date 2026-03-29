using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineBuilders;

public enum PipelineBuilderType { None, Script }

public record PipelineBuilder(Id<PipelineBuilder> Id, string Name, Id<Pipeline> PipelineId, PipelineBuilderType Type = PipelineBuilderType.None) : IHasId<Id<PipelineBuilder>>;
