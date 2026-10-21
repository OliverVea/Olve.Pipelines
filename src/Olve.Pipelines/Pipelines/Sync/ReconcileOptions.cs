namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Tuning for reconcile: how often bound repos are polled.
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
}
