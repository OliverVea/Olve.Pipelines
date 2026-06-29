namespace Olve.Pipelines.Cli.Api.Contracts;

/// <summary>Body for <c>PUT /api/pipelines/{pipelineId}/secrets/{name}</c> — sets a secret value.</summary>
public sealed class SetSecretRequest
{
    public required string Value { get; init; }
}
