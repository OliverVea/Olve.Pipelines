namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Retention for everything under <c>bundles/&lt;pipeline&gt;/&lt;bundle&gt;/</c> (step outputs + job logs).
/// A bundle is deleted only when it is BOTH older than <see cref="MinAge"/> AND not among its
/// pipeline's <see cref="KeepLatest"/> most recent bundles — so an idle pipeline always keeps its
/// recent history (and the bundle it last deployed) no matter how old. A bundle the promotion logic
/// may still run (a step's last success, the latest production success, or the bundle of a
/// scheduled/running job) is never deleted, whatever its age or rank.
/// </summary>
public record BundleRetentionOptions(bool Enabled, int KeepLatest, TimeSpan MinAge, TimeSpan Interval);

/// <summary>One bundle prefix in storage. <see cref="LastActivity"/> is its newest object's timestamp.</summary>
public record StoredBundle(string PipelineKey, string BundleKey, DateTimeOffset LastActivity, IReadOnlyList<string> ObjectKeys)
{
    public string Prefix => $"bundles/{PipelineKey}/{BundleKey}/";
}

public static class BundleRetentionPolicy
{
    /// <summary>
    /// The bundles to delete: per pipeline, all but the newest <c>keepLatest</c>, and only those older
    /// than <c>minAge</c> — minus any in <paramref name="inUse"/> (keyed like <see cref="StoredBundle"/>:
    /// pipeline and bundle id as 32 lowercase hex).
    /// </summary>
    public static IReadOnlyList<StoredBundle> SelectExpired(
        IEnumerable<StoredBundle> bundles, DateTimeOffset now, int keepLatest, TimeSpan minAge,
        IReadOnlySet<(string PipelineKey, string BundleKey)>? inUse = null) =>
        bundles
            .GroupBy(b => b.PipelineKey)
            .SelectMany(pipeline => pipeline
                .OrderByDescending(b => b.LastActivity)
                .Skip(keepLatest)
                .Where(b => now - b.LastActivity > minAge))
            .Where(b => inUse is null || !inUse.Contains((b.PipelineKey, b.BundleKey)))
            .ToList();

    /// <summary>
    /// Groups object listings into bundles. Only keys shaped <c>bundles/&lt;32 hex&gt;/&lt;32 hex&gt;/…</c> count;
    /// anything else under <c>bundles/</c> is never touched.
    /// </summary>
    public static IReadOnlyList<StoredBundle> GroupObjects(IEnumerable<(string Key, DateTimeOffset LastModified)> objects) =>
        objects
            .Select(o => (o.Key, o.LastModified, Parts: o.Key.Split('/', 4)))
            .Where(o => o.Parts.Length == 4 && o.Parts[0] == "bundles" && IsHexId(o.Parts[1]) && IsHexId(o.Parts[2]))
            .GroupBy(o => (Pipeline: o.Parts[1], Bundle: o.Parts[2]))
            .Select(g => new StoredBundle(g.Key.Pipeline, g.Key.Bundle, g.Max(o => o.LastModified), g.Select(o => o.Key).ToList()))
            .ToList();

    private static bool IsHexId(string s) => s.Length == 32 && s.All(char.IsAsciiHexDigitLower);
}
