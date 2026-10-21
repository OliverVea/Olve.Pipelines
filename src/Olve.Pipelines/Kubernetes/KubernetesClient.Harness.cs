using System.Net.Http.Json;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// The K8s calls the v2 harness executor needs beyond <see cref="IKubernetesClient"/>: submit a fully
/// built Job manifest, and read the terminal state of a Job's pod (per-container exit codes and
/// reasons, the Job's failure condition). A separate interface so existing fakes of
/// <see cref="IKubernetesClient"/> stay valid.
/// </summary>
public interface IHarnessKubernetesClient
{
    /// <summary>Creates <paramref name="manifest"/> in <paramref name="ns"/>. Throws on a non-success response.</summary>
    Task CreateJobAsync(string ns, K8sJobManifest manifest, CancellationToken ct = default);

    /// <summary>The Job's failure condition and its pod's container states; null when the Job does not exist.</summary>
    Task<HarnessPodState?> GetHarnessPodStateAsync(string ns, string jobName, CancellationToken ct = default);
}

public partial class KubernetesClient : IHarnessKubernetesClient
{
    public async Task CreateJobAsync(string ns, K8sJobManifest manifest, CancellationToken ct = default)
    {
        var name = manifest.Metadata.Name;
        // Never log the manifest itself.
        _logger?.LogInformation("Creating K8s Job {JobName} in namespace {Namespace}", name, ns);

        using var content = JsonContent.Create(manifest, HarnessKubernetesJsonContext.Default.K8sJobManifest);
        var response = await _httpClient.PostAsync($"apis/batch/v1/namespaces/{ns}/jobs", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("Failed to create Job {JobName}: {StatusCode} {Body}", name, response.StatusCode, body);
            throw new InvalidOperationException($"Failed to create K8s Job '{name}': {response.StatusCode}");
        }

        _logger?.LogInformation("K8s Job {JobName} created", name);
    }

    public async Task<HarnessPodState?> GetHarnessPodStateAsync(string ns, string jobName, CancellationToken ct = default)
    {
        var jobResponse = await _httpClient.GetAsync($"apis/batch/v1/namespaces/{ns}/jobs/{jobName}", ct);
        if (jobResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        jobResponse.EnsureSuccessStatusCode();
        var job = await jobResponse.Content.ReadFromJsonAsync(KubernetesJsonContext.Default.K8sJobResponse, ct);

        var podsResponse = await _httpClient.GetAsync(
            $"api/v1/namespaces/{ns}/pods?labelSelector=batch.kubernetes.io/job-name={jobName}", ct);
        podsResponse.EnsureSuccessStatusCode();
        var pods = await podsResponse.Content.ReadFromJsonAsync(KubernetesJsonContext.Default.K8sPodList, ct);

        return HarnessPodState.From(job, pods?.Items.FirstOrDefault());
    }
}
