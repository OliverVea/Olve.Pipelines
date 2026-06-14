namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Tuning for the reconcile drain. The timeout is deliberately generous (far longer than any
/// legitimate chain): a genuinely stuck <c>InProgress</c> job must never wedge new runs forever,
/// so on timeout the reconcile aborts, resumes the pipeline, and retries on the next poll.
/// </summary>
public record ReconcileOptions
{
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan DrainPollInterval { get; init; } = TimeSpan.FromSeconds(2);
}
