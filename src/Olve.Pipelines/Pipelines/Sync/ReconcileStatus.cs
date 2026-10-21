namespace Olve.Pipelines.Pipelines.Sync;

public enum ReconcileResult
{
    /// <summary>No reconcile has run yet (freshly bound).</summary>
    NeverRun,

    /// <summary>The last reconcile applied the repo config cleanly.</summary>
    Success,

    /// <summary>The last reconcile failed (fetch/compile/apply/drain) — live state is unchanged.</summary>
    Error,
}

/// <summary>
/// The outcome of the most recent reconcile attempt, surfaced via the status endpoint and the
/// frontend badge. <see cref="DeclaredSecrets"/> is the secret list from the last compiled config;
/// whether each is actually set in k8s is computed live at read time, not stored here (so setting a
/// secret reflects immediately rather than waiting for the next reconcile).
/// </summary>
/// <param name="Warnings">Manifest warnings of the last reconcile (null in snapshots written before B6).</param>
/// <param name="Sha">The config commit the last reconcile attempt was for.</param>
/// <param name="InProgress">A reconcile is running right now (not persisted meaningfully; cleared when it ends).</param>
public record ReconcileStatus(
    ReconcileResult Result,
    DateTimeOffset? LastSyncTime,
    IReadOnlyList<string> Problems,
    IReadOnlyList<SecretDeclaration> DeclaredSecrets,
    IReadOnlyList<string>? Warnings = null,
    string? Sha = null,
    bool InProgress = false)
{
    public static ReconcileStatus NeverRun { get; } = new(ReconcileResult.NeverRun, null, [], []);
}

/// <summary>
/// A secret declared by NAME ONLY. The value never lives in the repo — it stays in the
/// pipeline's own k8s secret <c>olve-pipeline-{id:N}</c>.
/// </summary>
public record SecretDeclaration(string Name, string? Description);
