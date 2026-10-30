using System.Text.Json.Serialization;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Runs;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Waiting), "waiting")]
[JsonDerivedType(typeof(Scheduled), "scheduled")]
[JsonDerivedType(typeof(InProgress), "in-progress")]
[JsonDerivedType(typeof(Succeeded), "succeeded")]
[JsonDerivedType(typeof(Failed), "failed")]
[JsonDerivedType(typeof(Skipped), "skipped")]
[JsonDerivedType(typeof(Cancelled), "cancelled")]
[JsonDerivedType(typeof(Obsolete), "obsolete")]
public abstract record AttemptStatus
{
    /// <summary>Waits for its external inputs; becomes <see cref="Scheduled"/> once all are answered.</summary>
    public record Waiting(DateTimeOffset Since) : AttemptStatus;
    /// <param name="SubmissionAttempts">Failed K8s submissions so far (bounded retry of the submit, not of the step).</param>
    public record Scheduled(int SubmissionAttempts = 0) : AttemptStatus;
    public record InProgress(DateTimeOffset StartedAt) : AttemptStatus;
    public record Succeeded(DateTimeOffset StartedAt, DateTimeOffset FinishedAt) : AttemptStatus;
    public record Failed(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string? Reason = null) : AttemptStatus;
    /// <param name="StartedAt">Null when the scheduler skipped it without running (<c>when</c>, upstream skip).</param>
    public record Skipped(string Reason, DateTimeOffset? StartedAt, DateTimeOffset FinishedAt) : AttemptStatus;
    public record Cancelled(DateTimeOffset? StartedAt, DateTimeOffset CancelledAt) : AttemptStatus;
    public record Obsolete(Id<Attempt> SupersedingAttemptId) : AttemptStatus;
}

public static class AttemptStatusExtensions
{
    public static bool IsActive(this AttemptStatus status) => status is AttemptStatus.Waiting or AttemptStatus.Scheduled or AttemptStatus.InProgress;

    public static bool IsTerminal(this AttemptStatus status) => !status.IsActive();

    /// <summary>
    /// Skipped by the scheduler without running (<c>when</c> false, upstream skipped) — as opposed to a
    /// handler that returned a skipped envelope. Scheduler skips never carry an input.
    /// </summary>
    public static bool IsSchedulerSkip(this Attempt attempt) => attempt.Status is AttemptStatus.Skipped && attempt.Input is null;

    public static Contract.AttemptState State(this AttemptStatus status) => status switch
    {
        AttemptStatus.Waiting => Contract.AttemptState.Waiting,
        AttemptStatus.Scheduled => Contract.AttemptState.Scheduled,
        AttemptStatus.InProgress => Contract.AttemptState.InProgress,
        AttemptStatus.Succeeded => Contract.AttemptState.Succeeded,
        AttemptStatus.Failed => Contract.AttemptState.Failed,
        AttemptStatus.Skipped => Contract.AttemptState.Skipped,
        AttemptStatus.Cancelled => Contract.AttemptState.Cancelled,
        AttemptStatus.Obsolete => Contract.AttemptState.Obsolete,
        _ => throw new InvalidOperationException($"Unknown attempt status {status.GetType().Name}."),
    };

    public static Contract.RunStepStatus ToStepStatus(this Contract.AttemptState state) => state switch
    {
        Contract.AttemptState.Waiting => Contract.RunStepStatus.Waiting,
        Contract.AttemptState.Scheduled => Contract.RunStepStatus.Scheduled,
        Contract.AttemptState.InProgress => Contract.RunStepStatus.InProgress,
        Contract.AttemptState.Succeeded => Contract.RunStepStatus.Succeeded,
        Contract.AttemptState.Failed => Contract.RunStepStatus.Failed,
        Contract.AttemptState.Skipped => Contract.RunStepStatus.Skipped,
        Contract.AttemptState.Cancelled => Contract.RunStepStatus.Cancelled,
        _ => Contract.RunStepStatus.Obsolete,
    };

    public static Contract.LogEndStatus ToLogEnd(this Contract.AttemptState state) => state switch
    {
        Contract.AttemptState.Waiting => Contract.LogEndStatus.Waiting,
        Contract.AttemptState.Scheduled => Contract.LogEndStatus.Scheduled,
        Contract.AttemptState.InProgress => Contract.LogEndStatus.InProgress,
        Contract.AttemptState.Succeeded => Contract.LogEndStatus.Succeeded,
        Contract.AttemptState.Failed => Contract.LogEndStatus.Failed,
        Contract.AttemptState.Skipped => Contract.LogEndStatus.Skipped,
        Contract.AttemptState.Cancelled => Contract.LogEndStatus.Cancelled,
        _ => Contract.LogEndStatus.Obsolete,
    };

    /// <summary>The wire discriminator (waiting / scheduled / in-progress / succeeded / failed / skipped / cancelled / obsolete).</summary>
    public static string Discriminator(this AttemptStatus status) => status switch
    {
        AttemptStatus.Waiting => "waiting",
        AttemptStatus.Scheduled => "scheduled",
        AttemptStatus.InProgress => "in-progress",
        AttemptStatus.Succeeded => "succeeded",
        AttemptStatus.Failed => "failed",
        AttemptStatus.Skipped => "skipped",
        AttemptStatus.Cancelled => "cancelled",
        AttemptStatus.Obsolete => "obsolete",
        _ => "idle",
    };

    public static DateTimeOffset? StartedAt(this Attempt attempt) => attempt.Status switch
    {
        AttemptStatus.InProgress s => s.StartedAt,
        AttemptStatus.Succeeded s => s.StartedAt,
        AttemptStatus.Failed s => s.StartedAt,
        AttemptStatus.Skipped s => s.StartedAt,
        AttemptStatus.Cancelled s => s.StartedAt,
        _ => null,
    };

    public static DateTimeOffset? FinishedAt(this Attempt attempt) => attempt.Status switch
    {
        AttemptStatus.Succeeded s => s.FinishedAt,
        AttemptStatus.Failed s => s.FinishedAt,
        AttemptStatus.Skipped s => s.FinishedAt,
        AttemptStatus.Cancelled s => s.CancelledAt,
        _ => null,
    };

    /// <summary>When the attempt last changed status (falls back to <see cref="Attempt.CreatedAt"/>).</summary>
    public static DateTimeOffset LastChangedAt(this Attempt attempt)
        => attempt.FinishedAt() ?? attempt.StartedAt() ?? attempt.CreatedAt;
}
