using System.Text.Json.Serialization;

namespace Olve.Pipelines.Jobs;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Scheduled), "scheduled")]
[JsonDerivedType(typeof(InProgress), "in-progress")]
[JsonDerivedType(typeof(Done), "done")]
[JsonDerivedType(typeof(Obsolete), "obsolete")]
[JsonDerivedType(typeof(Cancelled), "cancelled")]
[JsonDerivedType(typeof(Failed), "failed")]
public abstract record JobStatus
{
    public record Scheduled : JobStatus;
    public record InProgress(DateTimeOffset StartedAt) : JobStatus;
    public record Done(DateTimeOffset StartedAt, DateTimeOffset CompletedAt) : JobStatus;
    public record Obsolete(Id<Job> SupersedingJobId) : JobStatus;
    public record Cancelled(DateTimeOffset? StartedAt, DateTimeOffset CancelledAt) : JobStatus;
    public record Failed(DateTimeOffset StartedAt, DateTimeOffset FailedAt, string? Reason = null) : JobStatus;
}
