namespace Olve.Pipelines.Pipelines;

public interface IPipelineSecretStore
{
    Task<bool> HasSecretsAsync(Id<Pipeline> pipelineId, CancellationToken ct = default);
}

/// <summary>
/// Reads a single value out of a pipeline's K8s secret. Unlike <see cref="IPipelineSecretStore"/>
/// this is always registered (even when Kubernetes is not configured) so consumers can depend on it
/// unconditionally; it returns a failed <see cref="Result"/> rather than throwing when the cluster,
/// the secret, or the key is unavailable.
/// </summary>
public interface IPipelineSecretReader
{
    Task<Result<string>> TryGetSecretAsync(Id<Pipeline> pipelineId, string key, CancellationToken ct = default);
}
