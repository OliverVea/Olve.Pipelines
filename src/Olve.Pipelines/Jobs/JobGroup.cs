using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Jobs;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProductionJobGroup), "production")]
[JsonDerivedType(typeof(ProcessingJobGroup), "processing")]
public abstract record JobGroup(
    Id<JobGroup> Id,
    Id<Pipeline> PipelineId,
    DateTimeOffset CreatedAt) : IHasId<Id<JobGroup>>;

public record ProductionJobGroup(
    Id<JobGroup> Id,
    Id<Pipeline> PipelineId,
    DateTimeOffset CreatedAt,
    Id<ArtifactBundle> ArtifactBundleId) : JobGroup(Id, PipelineId, CreatedAt);

public record ProcessingJobGroup(
    Id<JobGroup> Id,
    Id<Pipeline> PipelineId,
    DateTimeOffset CreatedAt,
    Id<ArtifactBundle> ArtifactBundleId,
    Id<ProcessingStep> ProcessingStepId) : JobGroup(Id, PipelineId, CreatedAt);
