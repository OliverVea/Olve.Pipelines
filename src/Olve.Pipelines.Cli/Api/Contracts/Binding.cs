namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>Outcome of the most recent reconcile. Serialized as an integer; do not reorder.</summary>
public enum ReconcileResult
{
    NeverRun,
    Success,
    Error,
}

/// <summary>
/// CLI view of a pipeline's GitOps binding (a subset of the server shape). The HMAC
/// <c>webhookSecret</c> is intentionally not modeled — a secret the CLI never renders.
/// </summary>
public sealed class PipelineConfigBinding
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    public string? CredentialsSecret { get; set; }
    public string? LastPushSha { get; set; }
    public string? LastReconciledSha { get; set; }
    public ReconcileStatus Status { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReconcileStatus
{
    public ReconcileResult Result { get; set; }
    public DateTimeOffset? LastSyncTime { get; set; }
    public string[] Problems { get; set; } = [];
    public string[]? Warnings { get; set; }
    public string? Sha { get; set; }
    public bool InProgress { get; set; }
    public int? VersionNumber { get; set; }
}

/// <summary>CLI view of binding status (<c>GET /api/pipelines/{id}/binding/status</c>).</summary>
public sealed class PipelineBindingStatus
{
    public Guid PipelineId { get; set; }
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    public string? LastPushSha { get; set; }
    public string? LastReconciledSha { get; set; }
    public ReconcileResult Result { get; set; }
    public DateTimeOffset? LastSyncTime { get; set; }
    public string[] Problems { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public string? ReconcileSha { get; set; }
    public bool ReconcileInProgress { get; set; }
    public int? ReconciledVersion { get; set; }
    public PipelineActivation? Activation { get; set; }
    public PendingVersionInfo? Pending { get; set; }
}

/// <summary>Body for <c>POST /api/pipelines/with-repo</c> — create a pipeline already bound to a repo.</summary>
public sealed class CreatePipelineWithRepoRequest
{
    public required string Repo { get; init; }
    public string? Branch { get; init; }
    public string? Path { get; init; }
    public string? CredentialsSecret { get; init; }
}

/// <summary>Body for <c>PUT /api/pipelines/{id}/binding/branch</c>.</summary>
public sealed class SetBindingBranchRequest
{
    public required string Branch { get; init; }
}

/// <summary>Body for <c>PATCH /api/pipelines/{id}/binding</c> — set/clear the credentials secret key.</summary>
public sealed class UpdateBindingRequest
{
    public string? CredentialsSecret { get; init; }
}

