using Olve.Pipelines.Pipelines;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineSources;

public enum PipelineSourceType { None, Hardcoded, GitHub }

public record PipelineSource(Id<PipelineSource> Id, string Name, Id<Pipeline> PipelineId, PipelineSourceType Type = PipelineSourceType.None) : IHasId<Id<PipelineSource>>;
