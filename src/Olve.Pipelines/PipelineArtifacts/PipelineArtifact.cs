using Olve.Pipelines.PipelineBuilders;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineArtifacts;

public record PipelineArtifact(Id<PipelineArtifact> Id, string Name, Id<PipelineBuilder> BuilderId) : IHasId<Id<PipelineArtifact>>;
