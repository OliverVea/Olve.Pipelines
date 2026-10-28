namespace Olve.Pipelines.Pipelines.Sync;

public enum ReconcileResult
{
    /// <summary>No reconcile has run yet (freshly bound).</summary>
    NeverRun,

    /// <summary>The last reconcile applied the repo config cleanly.</summary>
    Success,

    /// <summary>The last reconcile failed (fetch/build/validate/switch); live state is unchanged.</summary>
    Error,
}

/// <summary>The outcome of the most recent reconcile attempt (binding status endpoint, UI badge).</summary>
/// <param name="Warnings">Manifest warnings of the last reconcile (null in older snapshots).</param>
/// <param name="Sha">The config commit the last reconcile attempt was for.</param>
/// <param name="InProgress">A reconcile is running right now.</param>
/// <param name="VersionNumber">The pipeline version the last successful reconcile switched to (null before one, and in older snapshots).</param>
public record ReconcileStatus(
    ReconcileResult Result,
    DateTimeOffset? LastSyncTime,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string>? Warnings = null,
    string? Sha = null,
    bool InProgress = false,
    int? VersionNumber = null)
{
    public static ReconcileStatus NeverRun { get; } = new(ReconcileResult.NeverRun, null, []);
}
