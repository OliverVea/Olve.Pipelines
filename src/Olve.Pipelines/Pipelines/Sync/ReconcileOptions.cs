namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Tuning for the reconcile drain. The timeout is deliberately generous (far longer than any
/// legitimate chain): a genuinely stuck <c>InProgress</c> job must never wedge new runs forever,
/// so on timeout the reconcile aborts, resumes the pipeline, and retries on the next poll.
/// </summary>
public record ReconcileOptions
{
    /// <summary>
    /// How often the deploy poll checks each bound repo for config/branch changes. Conservative by
    /// default — homelab CD doesn't need second-level responsiveness, and a longer interval keeps
    /// well clear of GitHub rate limits (the branch-head check is one counted request per cycle;
    /// the config check is a free 304 when unchanged). With webhook-driven deploys as the intended
    /// default, polling is the slow safety-net/fallback path, so the interval is relaxed to 15 min.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan DrainPollInterval { get; init; } = TimeSpan.FromSeconds(2);
}
