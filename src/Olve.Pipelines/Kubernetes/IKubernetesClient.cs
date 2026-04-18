namespace Olve.Pipelines.Kubernetes;

public interface IKubernetesClient
{
    Task CreateJobAsync(string ns, KubernetesJobSpec spec, CancellationToken ct = default);
    Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default);
    Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default);
    Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default);
    Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default);
    Task CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default);
    Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default);
    Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default);
    Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default);
}
