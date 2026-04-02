using System.Text.Json.Serialization;

namespace Olve.Pipelines.Jobs;

[JsonDerivedType(typeof(Scheduled), "scheduled")]
[JsonDerivedType(typeof(InProgress), "in-progress")]
[JsonDerivedType(typeof(Done), "done")]
[JsonDerivedType(typeof(Obsolete), "obsolete")]
[JsonDerivedType(typeof(Cancelled), "cancelled")]
public abstract record JobStatus
{
    public record Scheduled : JobStatus;
    public record InProgress(DateTimeOffset StartedAt) : JobStatus;
    public record Done(DateTimeOffset StartedAt, DateTimeOffset CompletedAt) : JobStatus;
    public record Obsolete(Id<Job> SupersedingJobId) : JobStatus;
    public record Cancelled(DateTimeOffset? StartedAt, DateTimeOffset CancelledAt) : JobStatus;
}
