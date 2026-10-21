using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// Placeholder unit of work kept so the scheduling/execution machinery (queue, obsoletion, runner,
/// K8s executor) stays alive between the v1 cut (B1) and the v2 Attempt model (B3/B4), which
/// replaces it. One job runs one named step of a pipeline as a single K8s Job.
/// </summary>
public record Job(
    Id<Job> Id,
    Id<Pipeline> PipelineId,
    string StepName,
    string Image,
    string Script,
    DateTimeOffset CreatedAt,
    JobStatus Status,
    Dictionary<string, string>? EnvironmentVariables = null) : IHasId<Id<Job>>
{
    /// <summary>The scheduling key: at most one InProgress job per (pipeline, step).</summary>
    [JsonIgnore]
    public JobKey Key => new(PipelineId, StepName);
}

public readonly record struct JobKey(Id<Pipeline> PipelineId, string StepName);
