using System.Text.Json;
using System.Net.Http.Json;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// The K8s calls the v2 harness executor needs beyond <see cref="IKubernetesClient"/>: submit a fully
/// built Job manifest, upsert the per-Job URL Secret, and read the terminal state of a Job's pod
/// (per-container exit codes and reasons, the Job's failure condition). A separate interface so
/// existing fakes of <see cref="IKubernetesClient"/> stay valid.
/// </summary>
public interface IHarnessKubernetesClient
{
    /// <summary>
    /// Creates <paramref name="manifest"/> in <paramref name="ns"/>. Returns false when a Job with that
    /// name already exists (409) — deterministic names make that "reattach", not an error. Throws on any
    /// other non-success response.
    /// </summary>
    Task<bool> CreateJobAsync(string ns, K8sJobManifest manifest, CancellationToken ct = default);

    /// <summary>Creates the Secret, or replaces its data when it already exists (fresh pre-signed URLs).</summary>
    Task UpsertSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default);

    /// <summary>The Job's failure condition and its pod's container states; null when the Job does not exist.</summary>
    Task<HarnessPodState?> GetHarnessPodStateAsync(string ns, string jobName, CancellationToken ct = default);

    /// <summary>Streams pod changes matching <paramref name="labelSelector"/> until the server ends the watch.</summary>
    IAsyncEnumerable<K8sPodItem> WatchPodsAsync(string ns, string labelSelector, CancellationToken ct = default);
}

public partial class KubernetesClient : IHarnessKubernetesClient
{
    public async Task<bool> CreateJobAsync(string ns, K8sJobManifest manifest, CancellationToken ct = default)
    {
        var name = manifest.Metadata.Name;
        // Never log the manifest itself.
        _logger?.LogInformation("Creating K8s Job {JobName} in namespace {Namespace}", name, ns);

        using var content = JsonContent.Create(manifest, HarnessKubernetesJsonContext.Default.K8sJobManifest);
        using var response = await _httpClient.PostAsync($"apis/batch/v1/namespaces/{ns}/jobs", content, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger?.LogInformation("K8s Job {JobName} already exists", name);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("Failed to create Job {JobName}: {StatusCode} {Body}", name, response.StatusCode, body);
            throw new HttpRequestException($"Failed to create K8s Job '{name}': {response.StatusCode}", null, response.StatusCode);
        }

        _logger?.LogInformation("K8s Job {JobName} created", name);
        return true;
    }

    public async Task UpsertSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
    {
        var secret = new K8sSecret("v1", "Secret", new K8sMetadata(secretName), Data: EncodeSecretData(data));
        using var response = await _httpClient.PostAsJsonAsync($"api/v1/namespaces/{ns}/secrets", secret, KubernetesJsonContext.Default.K8sSecret, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            await UpdateSecretAsync(ns, secretName, data, ct);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Failed to create K8s Secret '{secretName}': {response.StatusCode} {body}", null, response.StatusCode);
        }
    }

    public async Task<HarnessPodState?> GetHarnessPodStateAsync(string ns, string jobName, CancellationToken ct = default)
    {
        using var jobResponse = await _httpClient.GetAsync($"apis/batch/v1/namespaces/{ns}/jobs/{jobName}", ct);
        if (jobResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        jobResponse.EnsureSuccessStatusCode();
        var job = await jobResponse.Content.ReadFromJsonAsync(KubernetesJsonContext.Default.K8sJobResponse, ct);

        using var podsResponse = await _httpClient.GetAsync(
            $"api/v1/namespaces/{ns}/pods?labelSelector=batch.kubernetes.io/job-name={jobName}", ct);
        podsResponse.EnsureSuccessStatusCode();
        var pods = await podsResponse.Content.ReadFromJsonAsync(KubernetesJsonContext.Default.K8sPodList, ct);

        return HarnessPodState.From(job, pods?.Items.FirstOrDefault());
    }

    public async IAsyncEnumerable<K8sPodItem> WatchPodsAsync(string ns, string labelSelector,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Under HttpClient's 100s timeout; the caller reconnects.
        using var response = await _httpClient.GetAsync(
            $"api/v1/namespaces/{ns}/pods?watch=true&timeoutSeconds=60&labelSelector={Uri.EscapeDataString(labelSelector)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            var change = JsonSerializer.Deserialize(line, KubernetesJsonContext.Default.K8sPodWatchEvent);
            if (change?.Object is { } pod)
                yield return pod;
        }
    }
}
