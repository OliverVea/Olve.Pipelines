using System.Text.Json.Serialization;
using Olve.Pipelines.Building;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Sourcing;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Jobs;

[JsonDerivedType(typeof(SourcingJob), "sourcing")]
[JsonDerivedType(typeof(BuildJob), "build")]
[JsonDerivedType(typeof(ProcessingJob), "processing")]
public abstract record Job(Id<Job> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt, JobStatus Status) : IHasId<Id<Job>>
{
    public readonly record struct SourcingJobKey(Id<Pipeline> PipelineId);
    public readonly record struct BuildJobKey(Id<Pipeline> PipelineId);
    public readonly record struct ProcessingJobKey(Id<Pipeline> PipelineId, Id<ProcessingStep> ProcessingStepId);

    public record SourcingJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Result<Id<SourceBundle>>? SourcingResult = null) : Job(Id, PipelineId, CreatedAt, Status)
    {
        public SourcingJobKey JobKey => new(PipelineId);
    }

    public record BuildJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Id<SourceBundle> SourceBundleId,
        Result<Id<ArtifactBundle>>? BuildResult = null) : Job(Id, PipelineId, CreatedAt, Status)
    {
        public BuildJobKey JobKey => new(PipelineId);
    }

    public record ProcessingJob(
        Id<Job> Id,
        Id<Pipeline> PipelineId,
        DateTimeOffset CreatedAt,
        JobStatus Status,
        Id<ArtifactBundle> ArtifactBundleId,
        Id<ProcessingStep> ProcessingStepId,
        Result? ProcessingResult = null,
        IReadOnlyDictionary<Id<Verification>, Result?>? ValidationResults = null) : Job(Id, PipelineId, CreatedAt, Status)
    {
        public ProcessingJobKey JobKey => new(PipelineId, ProcessingStepId);
    }
}
