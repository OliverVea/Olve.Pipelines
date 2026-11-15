using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.Kubernetes;

/// <summary>The cluster's nodes and its unfinished pods' requests, which dispatch fits step pods into. Read-only.</summary>
public interface IKubernetesCapacity
{
    Task<ClusterView> ReadClusterAsync(CancellationToken ct = default);
}

public partial class KubernetesClient : IKubernetesCapacity
{
    private static readonly HashSet<string> CiRoles = ["ci-step", "ci-build"];

    public async Task<ClusterView> ReadClusterAsync(CancellationToken ct = default)
    {
        using var nodesResponse = await _httpClient.GetAsync("api/v1/nodes", ct);
        nodesResponse.EnsureSuccessStatusCode();
        var nodeList = await nodesResponse.Content.ReadFromJsonAsync(KubernetesCapacityJsonContext.Default.K8sNodeList, ct);
        var nodes = (nodeList?.Items ?? []).Select(n => new ClusterNode(
            n.Metadata.Name,
            n.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true,
            n.Spec?.Unschedulable == true,
            (n.Spec?.Taints ?? []).Select(t => new NodeTaint(t.Key, t.Value, t.Effect)).ToList(),
            n.Metadata.Labels ?? new Dictionary<string, string>(),
            ResourceAmount.Parse(n.Status?.Allocatable?.GetValueOrDefault("cpu"), n.Status?.Allocatable?.GetValueOrDefault("memory"))))
            .ToList();

        var pods = new List<ClusterPod>();
        string? token = null;
        do
        {
            var url = "api/v1/pods?fieldSelector=status.phase%21%3DSucceeded%2Cstatus.phase%21%3DFailed&limit=500"
                      + (token is null ? "" : $"&continue={Uri.EscapeDataString(token)}");
            using var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync(KubernetesCapacityJsonContext.Default.K8sCapacityPodList, ct);
            foreach (var pod in page?.Items ?? [])
            {
                var role = pod.Metadata.Labels?.GetValueOrDefault("olve-role");
                pods.Add(new ClusterPod(pod.Spec?.NodeName, Requests(pod.Spec), role is not null && CiRoles.Contains(role)));
            }
            token = string.IsNullOrEmpty(page?.Metadata?.Continue) ? null : page.Metadata.Continue;
        } while (token is not null);

        return new ClusterView(nodes, pods);
    }

    /// <summary>What the scheduler counts: the larger of the containers' sum and any one init container, plus the runtime's overhead.</summary>
    private static ResourceAmount Requests(K8sPodSpecRequests? spec)
    {
        if (spec is null) return ResourceAmount.Zero;
        static ResourceAmount Of(K8sContainerRequests c) =>
            ResourceAmount.Parse(c.Resources?.Requests?.GetValueOrDefault("cpu"), c.Resources?.Requests?.GetValueOrDefault("memory"));
        var containers = (spec.Containers ?? []).Aggregate(ResourceAmount.Zero, (sum, c) => sum + Of(c));
        foreach (var init in (spec.InitContainers ?? []).Select(Of))
            containers = new ResourceAmount(Math.Max(containers.CpuMillis, init.CpuMillis), Math.Max(containers.MemoryBytes, init.MemoryBytes));
        return containers + ResourceAmount.Parse(spec.Overhead?.GetValueOrDefault("cpu"), spec.Overhead?.GetValueOrDefault("memory"));
    }
}

public record K8sNodeTaint(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("effect")] string Effect);

public record K8sNodeSpec(
    [property: JsonPropertyName("unschedulable")] bool? Unschedulable = null,
    [property: JsonPropertyName("taints")] K8sNodeTaint[]? Taints = null);

public record K8sNodeCondition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status);

public record K8sNodeStatus(
    [property: JsonPropertyName("allocatable")] Dictionary<string, string>? Allocatable = null,
    [property: JsonPropertyName("conditions")] K8sNodeCondition[]? Conditions = null);

public record K8sNodeItem(
    [property: JsonPropertyName("metadata")] K8sObjectMeta Metadata,
    [property: JsonPropertyName("spec")] K8sNodeSpec? Spec = null,
    [property: JsonPropertyName("status")] K8sNodeStatus? Status = null);

public record K8sNodeList([property: JsonPropertyName("items")] K8sNodeItem[] Items);

public record K8sContainerResourceRequests([property: JsonPropertyName("requests")] Dictionary<string, string>? Requests = null);

public record K8sContainerRequests([property: JsonPropertyName("resources")] K8sContainerResourceRequests? Resources = null);

public record K8sPodSpecRequests(
    [property: JsonPropertyName("nodeName")] string? NodeName = null,
    [property: JsonPropertyName("containers")] K8sContainerRequests[]? Containers = null,
    [property: JsonPropertyName("initContainers")] K8sContainerRequests[]? InitContainers = null,
    [property: JsonPropertyName("overhead")] Dictionary<string, string>? Overhead = null);

public record K8sCapacityPodItem(
    [property: JsonPropertyName("metadata")] K8sObjectMeta Metadata,
    [property: JsonPropertyName("spec")] K8sPodSpecRequests? Spec = null);

public record K8sCapacityPodList(
    [property: JsonPropertyName("items")] K8sCapacityPodItem[] Items,
    [property: JsonPropertyName("metadata")] K8sListMeta? Metadata = null);

[JsonSerializable(typeof(K8sNodeList))]
[JsonSerializable(typeof(K8sCapacityPodList))]
internal partial class KubernetesCapacityJsonContext : JsonSerializerContext;
