using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// A scheduled unit of work: one named step of a pipeline. Interim placeholder shape (v2 B1) until
/// runs/attempts replace jobs.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public string StepName { get; set; } = "";
    public string Image { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public JobStatus Status { get; set; } = new ScheduledStatus();
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
