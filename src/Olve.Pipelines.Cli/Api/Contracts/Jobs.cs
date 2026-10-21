using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// One attempt of one step in a run (the server's <c>Attempt</c>; each runs as one K8s Job), as served
/// by <c>/api/jobs</c> (<c>pl job</c>, by attempt id) and the run-scoped attempt endpoints (<c>pl step</c>).
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public Guid RunId { get; set; }
    public int RunNumber { get; set; }
    public string StepName { get; set; } = "";
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public JobStatus Status { get; set; } = new ScheduledStatus();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScheduledStatus), "scheduled")]
[JsonDerivedType(typeof(InProgressStatus), "in-progress")]
[JsonDerivedType(typeof(SucceededStatus), "succeeded")]
[JsonDerivedType(typeof(FailedStatus), "failed")]
[JsonDerivedType(typeof(SkippedStatus), "skipped")]
[JsonDerivedType(typeof(CancelledStatus), "cancelled")]
[JsonDerivedType(typeof(ObsoleteStatus), "obsolete")]
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

public sealed class SucceededStatus : JobStatus
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    [JsonIgnore] public override string Kind => "succeeded";
}

public sealed class SkippedStatus : JobStatus
{
    public string Reason { get; set; } = "";
    [JsonIgnore] public override string Kind => "skipped";
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
    public string? Reason { get; set; }
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
