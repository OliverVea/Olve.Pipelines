using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Jobs;

[JsonDerivedType(typeof(ProductionJob), "production")]
[JsonDerivedType(typeof(ProcessingJob), "processing")]
public abstract record Job(Id<Job> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt, JobStatus Status) : IHasId<Id<Job>>
{
    public readonly record struct ProductionJobKey(Id<Pipeline> PipelineId);
    public readonly record struct ProcessingJobKey(Id<Pipeline> PipelineId, Id<ProcessingStep> ProcessingStepId);

    public record ProductionJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Result<Id<ArtifactBundle>>? ProductionResult = null) : Job(Id, PipelineId, CreatedAt, Status)
    {
        public ProductionJobKey JobKey => new(PipelineId);
    }

    public record ProcessingJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Id<ArtifactBundle> ArtifactBundleId,
        Id<ProcessingStep> ProcessingStepId,
        Result? ProcessingResult = null) : Job(Id, PipelineId, CreatedAt, Status)
    {
        public ProcessingJobKey JobKey => new(PipelineId, ProcessingStepId);
    }
}
