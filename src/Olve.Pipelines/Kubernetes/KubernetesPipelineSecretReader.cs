using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// Reads a key from a pipeline's K8s secret (<c>olve-pipeline-{id:N}</c>). The Kubernetes client is
/// resolved lazily from the container so this can be registered unconditionally: in deployments
/// without Kubernetes the registered client factory throws, and that surfaces here as a failed
/// <see cref="Result"/> instead of a startup crash.
/// </summary>
public class KubernetesPipelineSecretReader(IServiceProvider sp) : IPipelineSecretReader
{
    public async Task<Result<string>> TryGetSecretAsync(Id<Pipeline> pipelineId, string key, CancellationToken ct = default)
    {
        Dictionary<string, string>? secrets;
        try
        {
            var client = sp.GetRequiredService<KubernetesClient>();
            var options = sp.GetRequiredService<KubernetesOptions>();
            var secretName = PipelineSecrets.Name(pipelineId);
            secrets = await client.GetSecretAsync(options.Namespace, secretName, ct);
        }
        catch (Exception ex)
        {
            return Result.Failure<string>(new UnavailableProblem(ex, "Failed to read pipeline secret for '{0}'.", pipelineId));
        }

        if (secrets is null || !secrets.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
            return Result.Failure<string>(new NotFoundProblem("Pipeline '{0}' secret has no key '{1}'.", pipelineId, key));

        return value;
    }
}
