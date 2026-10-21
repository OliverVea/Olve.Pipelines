namespace Olve.Pipelines.Pipelines.Retention;

/// <summary>
/// Retention settings (<c>Retention:*</c>). A run — its <c>run.json</c>, outputs, files, logs and
/// failure-handler objects under <c>runs/&lt;p&gt;/&lt;run&gt;/</c> — is deleted only when it is BOTH older than
/// <see cref="MinRunAge"/> AND not among its pipeline's <see cref="KeepLatestRuns"/> newest runs (v1's rule:
/// 6 months / latest 20). A compiled binary (<c>step.tar.gz</c>) is kept while a pipeline's active/draining
/// version or any retained run's version uses it, and otherwise deleted once older than <see cref="BuildGrace"/>.
/// <see cref="DryRun"/> (the default) only logs what would be deleted.
/// </summary>
/// <param name="OrphanGrace">How old leftovers nothing references must be before they go: objects of runs
/// that no longer exist (e.g. a deleted pipeline's), stale trigger events.</param>
/// <param name="ResultArchiveGrace">Leftover <c>result.tar.gz</c> of finished attempts (normally deleted on collection).</param>
/// <param name="ClusterGrace">Minimum age of a URL Secret or a leaked step Job before it may be deleted.</param>
/// <param name="FinishedJobMaxAge">Finished v2 Jobs without a TTL are deleted after this.</param>
public sealed record RetentionOptions(
    bool Enabled = true,
    bool DryRun = true,
    int KeepLatestRuns = 20,
    TimeSpan MinRunAge = default,
    TimeSpan BuildGrace = default,
    TimeSpan OrphanGrace = default,
    TimeSpan ResultArchiveGrace = default,
    TimeSpan ClusterGrace = default,
    TimeSpan FinishedJobMaxAge = default,
    TimeSpan Interval = default,
    TimeSpan StartupDelay = default)
{
    public static RetentionOptions Defaults { get; } = new(
        MinRunAge: TimeSpan.FromDays(183),
        BuildGrace: TimeSpan.FromDays(7),
        OrphanGrace: TimeSpan.FromDays(7),
        ResultArchiveGrace: TimeSpan.FromDays(1),
        ClusterGrace: TimeSpan.FromHours(1),
        FinishedJobMaxAge: TimeSpan.FromDays(7),
        Interval: TimeSpan.FromHours(24),
        StartupDelay: TimeSpan.FromMinutes(2));

    /// <summary>Binds <c>Retention:*</c> (day/hour/minute-valued keys) over <see cref="Defaults"/>.</summary>
    public static RetentionOptions From(IConfiguration configuration)
    {
        var s = configuration.GetSection("Retention");
        var d = Defaults;
        TimeSpan Days(string key, TimeSpan fallback) => s.GetValue<double?>(key) is { } v ? TimeSpan.FromDays(v) : fallback;
        TimeSpan Hours(string key, TimeSpan fallback) => s.GetValue<double?>(key) is { } v ? TimeSpan.FromHours(v) : fallback;
        TimeSpan Minutes(string key, TimeSpan fallback) => s.GetValue<double?>(key) is { } v ? TimeSpan.FromMinutes(v) : fallback;

        return new RetentionOptions(
            Enabled: s.GetValue("Enabled", d.Enabled),
            DryRun: s.GetValue("DryRun", d.DryRun),
            KeepLatestRuns: s.GetValue("KeepLatestRuns", d.KeepLatestRuns),
            MinRunAge: Days("MinRunAgeDays", d.MinRunAge),
            BuildGrace: Days("BuildGraceDays", d.BuildGrace),
            OrphanGrace: Days("OrphanGraceDays", d.OrphanGrace),
            ResultArchiveGrace: Hours("ResultArchiveGraceHours", d.ResultArchiveGrace),
            ClusterGrace: Minutes("ClusterGraceMinutes", d.ClusterGrace),
            FinishedJobMaxAge: Days("FinishedJobMaxAgeDays", d.FinishedJobMaxAge),
            Interval: Hours("IntervalHours", d.Interval),
            StartupDelay: Minutes("StartupDelayMinutes", d.StartupDelay));
    }
}
