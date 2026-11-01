namespace Olve.Pipelines.Kubernetes;

public sealed record SecretRevision(Dictionary<string, string> Data, string ResourceVersion);

public interface IKubernetesClient
{
    Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default);
    Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default);
    Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default);
    Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default);
    /// <summary>False when the Secret already exists: its values are kept.</summary>
    Task<bool> CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default);
    Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default);
    Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default);
    Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default);

    /// <summary>The Secret's values and version, null when it doesn't exist. No values is an empty dictionary.</summary>
    Task<SecretRevision?> GetSecretRevisionAsync(string ns, string secretName, CancellationToken ct = default);

    /// <summary>Replaces the values only if the Secret is still at <paramref name="resourceVersion"/>; false when it changed or is gone.</summary>
    Task<bool> TryUpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, string resourceVersion, CancellationToken ct = default);

    /// <summary>Deletes the Secret only if it is still at <paramref name="resourceVersion"/>; false when it changed. A missing one counts as deleted.</summary>
    Task<bool> TryDeleteSecretAsync(string ns, string secretName, string resourceVersion, CancellationToken ct = default);
}
