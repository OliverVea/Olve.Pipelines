using Olve.Pipelines.PipelineBuilds;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.PipelineArtifacts;

public record PipelineArtifact(Id<PipelineArtifact> Id, string Name, Id<PipelineBuild> BuildId) : IHasId<Id<PipelineArtifact>>;
