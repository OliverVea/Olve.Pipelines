using Olve.Pipelines.Pipelines;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Building;

public enum ArtifactBundleStatus { Pending, Completed, Failed }

public record ArtifactBundle(
    Id<ArtifactBundle> Id,
    Id<Pipeline> PipelineId,
    DateTimeOffset CreatedAt,
    ArtifactBundleStatus Status) : IHasId<Id<ArtifactBundle>>;
