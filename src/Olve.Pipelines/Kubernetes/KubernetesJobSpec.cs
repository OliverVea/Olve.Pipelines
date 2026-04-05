namespace Olve.Pipelines.Kubernetes;

public record KubernetesJobSpec(
    string Name,
    string Image,
    string Script,
    string OutputBundleS3Prefix,
    string S3HelperImage,
    string S3Bucket,
    string S3Endpoint,
    string S3CredentialsSecretName,
    bool S3SkipCertValidation = false,
    Dictionary<string, string>? EnvironmentVariables = null,
    string? SecretName = null,
    string? InputBundleS3Prefix = null);
