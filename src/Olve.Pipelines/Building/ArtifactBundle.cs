using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Sourcing;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Building;

public enum ArtifactBundleStatus { Pending, Completed, Failed }

public record ArtifactBundle(
    Id<ArtifactBundle> Id,
    Id<Pipeline> PipelineId,
    Id<SourceBundle> SourceBundleId,
    DateTimeOffset CreatedAt,
    ArtifactBundleStatus Status) : IHasId<Id<ArtifactBundle>>;
