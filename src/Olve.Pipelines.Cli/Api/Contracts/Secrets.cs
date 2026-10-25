namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>Body for <c>PUT /api/pipelines/{pipelineId}/secrets/{name}</c> — sets a secret value.</summary>
public sealed class SetSecretRequest
{
    public required string Value { get; init; }
}

/// <summary>
/// Body for <c>POST /api/pipelines/{pipelineId}/secrets/copy</c> — copy secrets from another pipeline
/// server-side. <see cref="Names"/> null copies all; <see cref="TargetName"/> renames a single copy.
/// </summary>
public sealed class CopySecretsRequest
{
    public required Guid SourcePipelineId { get; init; }
    public string[]? Names { get; init; }
    public string? TargetName { get; init; }
    public bool Overwrite { get; init; }
}

/// <summary>Response of the copy: the target secret names written (never values).</summary>
public sealed class CopySecretsResponse
{
    public string[] Copied { get; init; } = [];
}

/// <summary>One entry of <c>GET /api/pipelines/{pipelineId}/secrets</c>. <see cref="IsSet"/> null = K8s unreadable.</summary>
public sealed class PipelineSecretInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string[] DeclaredBy { get; set; } = [];
    public bool? IsSet { get; set; }
    public bool? Needed { get; set; }
}
