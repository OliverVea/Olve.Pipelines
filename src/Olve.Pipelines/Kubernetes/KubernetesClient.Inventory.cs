using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Retention;

namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// Read-only listings retention needs: Jobs labelled <c>app=olve-pipelines</c> and Secret metadata (names and
/// creation times — never data). A separate interface so existing fakes of the other K8s seams stay valid.
/// Deletes go through <see cref="IKubernetesClient"/>.
/// </summary>
public interface IKubernetesInventory
{
    Task<IReadOnlyList<ClusterJob>> ListJobsAsync(string ns, CancellationToken ct = default);

    Task<IReadOnlyList<ClusterSecret>> ListSecretsAsync(string ns, CancellationToken ct = default);
}

public partial class KubernetesClient : IKubernetesInventory
{
    private const int ListPageSize = 500;

    public async Task<IReadOnlyList<ClusterJob>> ListJobsAsync(string ns, CancellationToken ct = default)
    {
        var result = new List<ClusterJob>();
        string? token = null;
        do
        {
            var url = $"apis/batch/v1/namespaces/{ns}/jobs?labelSelector=app%3Dolve-pipelines&limit={ListPageSize}"
                      + (token is null ? "" : $"&continue={Uri.EscapeDataString(token)}");
            using var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync(KubernetesInventoryJsonContext.Default.K8sJobList, ct);
            foreach (var item in page?.Items ?? [])
            {
                var finished = item.Status?.Conditions?.Any(c => c.Type is "Complete" or "Failed" && c.Status == "True") == true;
                result.Add(new ClusterJob(item.Metadata.Name, item.Metadata.Labels ?? new Dictionary<string, string>(),
                    item.Metadata.CreationTimestamp ?? DateTimeOffset.MinValue, finished, item.Spec?.TtlSecondsAfterFinished));
            }
            token = string.IsNullOrEmpty(page?.Metadata?.Continue) ? null : page.Metadata.Continue;
        } while (token is not null);

        return result;
    }

    public async Task<IReadOnlyList<ClusterSecret>> ListSecretsAsync(string ns, CancellationToken ct = default)
    {
        var result = new List<ClusterSecret>();
        string? token = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"api/v1/namespaces/{ns}/secrets?limit={ListPageSize}" + (token is null ? "" : $"&continue={Uri.EscapeDataString(token)}"));
            // Metadata only: the API server never sends secret data for a PartialObjectMetadataList.
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;as=PartialObjectMetadataList;g=meta.k8s.io;v=v1"));
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync(KubernetesInventoryJsonContext.Default.K8sMetadataList, ct);
            foreach (var item in page?.Items ?? [])
                result.Add(new ClusterSecret(item.Metadata.Name, item.Metadata.CreationTimestamp ?? DateTimeOffset.MinValue));
            token = string.IsNullOrEmpty(page?.Metadata?.Continue) ? null : page.Metadata.Continue;
        } while (token is not null);

        return result;
    }
}

public record K8sObjectMeta(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels = null,
    [property: JsonPropertyName("creationTimestamp")] DateTimeOffset? CreationTimestamp = null);

public record K8sListMeta([property: JsonPropertyName("continue")] string? Continue = null);

public record K8sJobListSpec([property: JsonPropertyName("ttlSecondsAfterFinished")] int? TtlSecondsAfterFinished = null);

public record K8sJobListItem(
    [property: JsonPropertyName("metadata")] K8sObjectMeta Metadata,
    [property: JsonPropertyName("spec")] K8sJobListSpec? Spec = null,
    [property: JsonPropertyName("status")] K8sJobStatusBody? Status = null);

public record K8sJobList(
    [property: JsonPropertyName("items")] K8sJobListItem[] Items,
    [property: JsonPropertyName("metadata")] K8sListMeta? Metadata = null);

public record K8sMetadataItem([property: JsonPropertyName("metadata")] K8sObjectMeta Metadata);

public record K8sMetadataList(
    [property: JsonPropertyName("items")] K8sMetadataItem[] Items,
    [property: JsonPropertyName("metadata")] K8sListMeta? Metadata = null);

[JsonSerializable(typeof(K8sJobList))]
[JsonSerializable(typeof(K8sMetadataList))]
internal partial class KubernetesInventoryJsonContext : JsonSerializerContext;
