using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes;

public class KubernetesPipelineSecretStore(KubernetesClient kubernetesClient, KubernetesOptions options) : IPipelineSecretStore
{
    public async Task<bool> HasSecretsAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
    {
        var secretName = $"olve-pipeline-{pipelineId.Value.Value:N}";
        var data = await kubernetesClient.GetSecretAsync(options.Namespace, secretName, ct);
        return data is not null && data.Count > 0;
    }
}
