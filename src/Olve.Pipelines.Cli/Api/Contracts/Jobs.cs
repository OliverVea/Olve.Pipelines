using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// A scheduled unit of work. Polymorphic on the server's <c>type</c> discriminator. Modeled with
/// native <see cref="JsonPolymorphicAttribute"/> (AOT-safe), unlike the generated client's
/// reflection-based converter. <see cref="Kind"/> is a non-serialized label for human output.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProductionJob), "production")]
[JsonDerivedType(typeof(ProcessingJob), "processing")]
public abstract class Job
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public Guid JobGroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public JobStatus Status { get; set; } = new ScheduledStatus();

    [JsonIgnore] public abstract string Kind { get; }
}

public sealed class ProductionJob : Job
{
    public Guid ProductionStepId { get; set; }
    [JsonIgnore] public override string Kind => "production";
}

public sealed class ProcessingJob : Job
{
    public Guid ProcessingStepId { get; set; }
    public Guid ArtifactBundleId { get; set; }
    [JsonIgnore] public override string Kind => "processing";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScheduledStatus), "scheduled")]
[JsonDerivedType(typeof(InProgressStatus), "in-progress")]
[JsonDerivedType(typeof(DoneStatus), "done")]
[JsonDerivedType(typeof(ObsoleteStatus), "obsolete")]
[JsonDerivedType(typeof(CancelledStatus), "cancelled")]
[JsonDerivedType(typeof(FailedStatus), "failed")]
public abstract class JobStatus
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed class ScheduledStatus : JobStatus
{
    [JsonIgnore] public override string Kind => "scheduled";
}

public sealed class InProgressStatus : JobStatus
{
    public DateTimeOffset? StartedAt { get; set; }
    [JsonIgnore] public override string Kind => "in-progress";
}

public sealed class DoneStatus : JobStatus
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    [JsonIgnore] public override string Kind => "done";
}

public sealed class ObsoleteStatus : JobStatus
{
    [JsonIgnore] public override string Kind => "obsolete";
}

public sealed class CancelledStatus : JobStatus
{
    public DateTimeOffset CancelledAt { get; set; }
    [JsonIgnore] public override string Kind => "cancelled";
}

public sealed class FailedStatus : JobStatus
{
    [JsonIgnore] public override string Kind => "failed";
}

/// <summary>A page of jobs from the paginated jobs endpoint.</summary>
public sealed class PageOfJob
{
    public Job[] Items { get; set; } = [];
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasNextPage { get; set; }
}
