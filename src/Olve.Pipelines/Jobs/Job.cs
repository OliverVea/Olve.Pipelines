using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Jobs;

[JsonDerivedType(typeof(ProductionJob), "production")]
[JsonDerivedType(typeof(ProcessingJob), "processing")]
public abstract record Job(Id<Job> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt, JobStatus Status, Id<JobGroup> JobGroupId) : IHasId<Id<Job>>
{
    public readonly record struct ProductionJobKey(Id<Pipeline> PipelineId, Id<ProductionStep> ProductionStepId);
    public readonly record struct ProcessingJobKey(Id<Pipeline> PipelineId, Id<ProcessingStep> ProcessingStepId);

    public record ProductionJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Id<JobGroup> JobGroupId,
        Id<ProductionStep> ProductionStepId) : Job(Id, PipelineId, CreatedAt, Status, JobGroupId)
    {
        public ProductionJobKey JobKey => new(PipelineId, ProductionStepId);
    }

    public record ProcessingJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Id<JobGroup> JobGroupId,
        Id<ArtifactBundle> ArtifactBundleId,
        Id<ProcessingStep> ProcessingStepId,
        Result? ProcessingResult = null) : Job(Id, PipelineId, CreatedAt, Status, JobGroupId)
    {
        public ProcessingJobKey JobKey => new(PipelineId, ProcessingStepId);
    }
}
