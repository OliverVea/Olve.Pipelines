using System.Text.Json.Serialization;

namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// One attempt of one step in a run (the server's <c>Attempt</c>; each runs as one K8s Job), as served
/// by the run-scoped attempt endpoints (<c>pl step</c>, <c>pl run</c>).
/// </summary>
public sealed class Attempt
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public Guid RunId { get; set; }
    public int RunNumber { get; set; }
    public string StepName { get; set; } = "";
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public AttemptStatus Status { get; set; } = new ScheduledStatus();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScheduledStatus), "scheduled")]
[JsonDerivedType(typeof(InProgressStatus), "in-progress")]
[JsonDerivedType(typeof(SucceededStatus), "succeeded")]
[JsonDerivedType(typeof(FailedStatus), "failed")]
[JsonDerivedType(typeof(SkippedStatus), "skipped")]
[JsonDerivedType(typeof(CancelledStatus), "cancelled")]
[JsonDerivedType(typeof(ObsoleteStatus), "obsolete")]
public abstract class AttemptStatus
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed class ScheduledStatus : AttemptStatus
{
    [JsonIgnore] public override string Kind => "scheduled";
}

public sealed class InProgressStatus : AttemptStatus
{
    public DateTimeOffset? StartedAt { get; set; }
    [JsonIgnore] public override string Kind => "in-progress";
}

public sealed class SucceededStatus : AttemptStatus
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    [JsonIgnore] public override string Kind => "succeeded";
}

public sealed class SkippedStatus : AttemptStatus
{
    public string Reason { get; set; } = "";
    [JsonIgnore] public override string Kind => "skipped";
}

public sealed class ObsoleteStatus : AttemptStatus
{
    [JsonIgnore] public override string Kind => "obsolete";
}

public sealed class CancelledStatus : AttemptStatus
{
    public DateTimeOffset CancelledAt { get; set; }
    [JsonIgnore] public override string Kind => "cancelled";
}

public sealed class FailedStatus : AttemptStatus
{
    public string? Reason { get; set; }
    [JsonIgnore] public override string Kind => "failed";
}

/// <summary>A scheduled or in-progress attempt anywhere on the instance (<c>GET /api/attempts</c>, <c>pl attempts</c>).</summary>
public sealed class ActiveAttempt
{
    public Guid PipelineId { get; set; }
    public string PipelineName { get; set; } = "";
    public int Run { get; set; }
    public string Step { get; set; } = "";
    public int Attempt { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
}
