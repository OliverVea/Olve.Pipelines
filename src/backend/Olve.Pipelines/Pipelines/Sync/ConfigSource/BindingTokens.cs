using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

/// <summary>A binding's GitHub token, read from the pipeline's Secret at call time; null for a public repo or when it can't be read.</summary>
internal static class BindingTokens
{
    public static async Task<string?> GetAsync(
        IKubernetesClient kubernetes, KubernetesOptions options, PipelineConfigBinding binding, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(binding.CredentialsSecret))
            return null;

        try
        {
            var secrets = await kubernetes.GetSecretAsync(options.Namespace, PipelineSecrets.Name(binding.PipelineId), ct);
            if (secrets is not null && secrets.TryGetValue(binding.CredentialsSecret, out var token))
                return token;

            logger.LogWarning(
                "Credentials secret key '{Key}' not found for pipeline '{PipelineId}'", binding.CredentialsSecret, binding.PipelineId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve credentials for pipeline '{PipelineId}'", binding.PipelineId);
            return null;
        }
    }
}
