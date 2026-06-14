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
public record ReconcileStatus(
    ReconcileResult Result,
    DateTimeOffset? LastSyncTime,
    IReadOnlyList<string> Problems,
    IReadOnlyList<SecretDeclaration> DeclaredSecrets)
{
    public static ReconcileStatus NeverRun { get; } = new(ReconcileResult.NeverRun, null, [], []);
}
