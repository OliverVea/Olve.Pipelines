namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// How often bound repos are polled.
/// </summary>
public record ReconcileOptions
{
    /// <summary>
    /// How often each polling binding is checked: its config, its branch head and, when CI has authors, its pull
    /// requests. Conservative by default — homelab CD doesn't need second-level responsiveness, and a longer interval
    /// keeps well clear of GitHub rate limits (the branch-head check and the pull request list are one counted request
    /// each per cycle; the config check is a free 304 when unchanged). Webhooks make all of it faster; polling is the
    /// safety net, so the interval is relaxed to 15 min.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(15);
}
