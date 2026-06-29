namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>
/// How a bound pipeline learns it should deploy. Serialized as an integer (0=Webhook,
/// 1=WebhookOnly, 2=Poll), matching the server enum — do not reorder.
/// </summary>
public enum BindingDeployTrigger
{
    Webhook,
    WebhookOnly,
    Poll,
}

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
    public string? LastDeployedSha { get; set; }
    public string? LastSyncedSha { get; set; }
    public ReconcileStatus Status { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public BindingDeployTrigger DeployTrigger { get; set; }
}

public sealed class ReconcileStatus
{
    public ReconcileResult Result { get; set; }
    public DateTimeOffset? LastSyncTime { get; set; }
    public string[] Problems { get; set; } = [];
    public SecretDeclaration[] DeclaredSecrets { get; set; } = [];
}

public sealed class SecretDeclaration
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>CLI view of binding status (<c>GET /api/pipelines/{id}/binding/status</c>).</summary>
public sealed class PipelineBindingStatus
{
    public Guid PipelineId { get; set; }
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    public string? LastDeployedSha { get; set; }
    public string? LastSyncedSha { get; set; }
    public ReconcileResult Result { get; set; }
    public DateTimeOffset? LastSyncTime { get; set; }
    public string[] Problems { get; set; } = [];
    public SecretStatus[] Secrets { get; set; } = [];
}

/// <summary><see cref="IsSet"/> is null when k8s could not be read (unknown), not false.</summary>
public sealed class SecretStatus
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool? IsSet { get; set; }
}

/// <summary>Body for <c>POST /api/pipelines/with-repo</c> — create a pipeline already bound to a repo.</summary>
public sealed class CreatePipelineWithRepoRequest
{
    public required string Repo { get; init; }
    public string? Branch { get; init; }
    public string? Path { get; init; }
    public string? CredentialsSecret { get; init; }
    public BindingDeployTrigger? DeployTrigger { get; init; }
}

/// <summary>Body for <c>PATCH /api/pipelines/{id}/binding</c> — set/clear the credentials secret key.</summary>
public sealed class UpdateBindingRequest
{
    public string? CredentialsSecret { get; init; }
}

/// <summary>Body for <c>PATCH /api/pipelines/{id}/binding/deploy-trigger</c>.</summary>
public sealed class SetDeployTriggerRequest
{
    public required BindingDeployTrigger DeployTrigger { get; init; }
}
