namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Reads one value of a pipeline's K8s Secret. Always registered, even without Kubernetes: unavailable
/// cluster, secret or key is a failed <see cref="Result"/>, never an exception.
/// </summary>
public interface IPipelineSecretReader
{
    Task<Result<string>> TryGetSecretAsync(Id<Pipeline> pipelineId, string key, CancellationToken ct = default);
}
