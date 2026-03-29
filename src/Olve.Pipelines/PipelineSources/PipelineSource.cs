using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineSources;

public record PipelineSource(Id<PipelineSource> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<PipelineSource>>;
