namespace Olve.Pipelines.Kubernetes;

public record KubernetesJobSpec(
    string Name,
    string Image,
    string Script,
    Dictionary<string, string>? EnvironmentVariables = null,
    string? SecretName = null,
    string? InputBundleS3Key = null,
    string? OutputBundleS3Key = null);
