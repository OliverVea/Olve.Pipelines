using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineBuilders;

public record PipelineBuilder(Id<PipelineBuilder> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<PipelineBuilder>>;
