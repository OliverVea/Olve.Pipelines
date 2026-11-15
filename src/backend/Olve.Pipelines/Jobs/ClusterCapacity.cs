using System.Globalization;

namespace Olve.Pipelines.Jobs;

/// <summary>CPU (millicores) and memory (bytes): a pod's requests, a node's allocatable, or what is left of it.</summary>
public readonly record struct ResourceAmount(long CpuMillis, long MemoryBytes)
{
    public static readonly ResourceAmount Zero = new(0, 0);

    public static ResourceAmount operator +(ResourceAmount a, ResourceAmount b) => new(a.CpuMillis + b.CpuMillis, a.MemoryBytes + b.MemoryBytes);
    public static ResourceAmount operator -(ResourceAmount a, ResourceAmount b) => new(a.CpuMillis - b.CpuMillis, a.MemoryBytes - b.MemoryBytes);

    public bool Holds(ResourceAmount request) => request.CpuMillis <= CpuMillis && request.MemoryBytes <= MemoryBytes;

    public ResourceAmount Scale(double share) => new((long)(CpuMillis * share), (long)(MemoryBytes * share));

    /// <summary>From Kubernetes quantities (<c>"1"</c>, <c>"500m"</c>; <c>"1536Mi"</c>, <c>"2Gi"</c>, <c>"49332412Ki"</c>); a missing one is 0.</summary>
    public static ResourceAmount Parse(string? cpu, string? memory) => new(ParseCpuMillis(cpu), ParseBytes(memory));

    public static long ParseCpuMillis(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return 0;
        return quantity.EndsWith('m')
            ? (long)decimal.Parse(quantity[..^1], CultureInfo.InvariantCulture)
            : (long)(decimal.Parse(quantity, CultureInfo.InvariantCulture) * 1000);
    }

    private static readonly (string Suffix, decimal Factor)[] MemorySuffixes =
    [
        ("Ki", 1024m), ("Mi", 1024m * 1024), ("Gi", 1024m * 1024 * 1024), ("Ti", 1024m * 1024 * 1024 * 1024),
        ("k", 1000m), ("M", 1000m * 1000), ("G", 1000m * 1000 * 1000), ("T", 1000m * 1000 * 1000 * 1000),
        ("m", 0.001m),
    ];

    public static long ParseBytes(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return 0;
        foreach (var (suffix, factor) in MemorySuffixes)
            if (quantity.EndsWith(suffix, StringComparison.Ordinal))
                return (long)(decimal.Parse(quantity[..^suffix.Length], CultureInfo.InvariantCulture) * factor);
        return (long)decimal.Parse(quantity, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

public sealed record NodeTaint(string Key, string? Value, string Effect);

public sealed record ClusterNode(
    string Name,
    bool Ready,
    bool Unschedulable,
    IReadOnlyList<NodeTaint> Taints,
    IReadOnlyDictionary<string, string> Labels,
    ResourceAmount Allocatable);

/// <summary>A pod that is not finished: what it requests, where it runs (null = not placed yet), and whether it is CI's.</summary>
public sealed record ClusterPod(string? Node, ResourceAmount Requests, bool Ci);

public sealed record ClusterView(IReadOnlyList<ClusterNode> Nodes, IReadOnlyList<ClusterPod> Pods);

/// <param name="NodeReserve">Kept free on every node that isn't a helper, for the apps and the host (<c>Jobs:NodeReserve</c>).</param>
/// <param name="CiShare">The share of the usable capacity CI's pods may request at once (<c>Jobs:CiCapacityShare</c>).</param>
/// <param name="Helper">The helper nodes' label, whose <c>NoSchedule</c> taint step pods tolerate; null = none.</param>
public sealed record CapacityOptions(ResourceAmount NodeReserve, double CiShare, (string Key, string Value)? Helper);

/// <summary>Where a pod with a request still fits, and taking that room once it starts.</summary>
public interface ICapacityPlan
{
    /// <summary>The node a pod with this request would fit on, or null when none has room (or CI's share is used up).</summary>
    string? Fits(ResourceAmount request, bool ci);

    void Reserve(string node, ResourceAmount request, bool ci);
}

/// <summary>
/// What step pods may still request, per node they may use, and what CI may still request. Built from one
/// <see cref="ClusterView"/>; <see cref="Reserve"/> takes a started pod's request off, so one dispatch pass
/// never hands out the same room twice.
/// </summary>
public sealed class CapacityPlan : ICapacityPlan
{
    private readonly List<(string Name, bool Helper, ResourceAmount Free)> _nodes;
    private ResourceAmount _ciUsed;
    private readonly ResourceAmount _ciLimit;

    private CapacityPlan(List<(string, bool, ResourceAmount)> nodes, ResourceAmount ciUsed, ResourceAmount ciLimit)
    {
        _nodes = nodes;
        _ciUsed = ciUsed;
        _ciLimit = ciLimit;
    }

    public static CapacityPlan Create(ClusterView view, CapacityOptions options)
    {
        var usable = view.Nodes.Where(n => Usable(n, options.Helper)).ToList();
        var nodes = usable.Select(n =>
        {
            var helper = IsHelper(n, options.Helper);
            var requested = view.Pods.Where(p => p.Node == n.Name).Aggregate(ResourceAmount.Zero, (sum, p) => sum + p.Requests);
            return (n.Name, helper, Free: n.Allocatable - requested - (helper ? ResourceAmount.Zero : options.NodeReserve));
        }).ToList();

        foreach (var pod in view.Pods.Where(p => p.Node is null))
        {
            var i = nodes.FindIndex(n => n.Free.MemoryBytes == nodes.Max(m => m.Free.MemoryBytes));
            if (i >= 0) nodes[i] = nodes[i] with { Free = nodes[i].Free - pod.Requests };
        }

        var capacity = usable.Aggregate(ResourceAmount.Zero, (sum, n) =>
            sum + n.Allocatable - (IsHelper(n, options.Helper) ? ResourceAmount.Zero : options.NodeReserve));
        var ciUsed = view.Pods.Where(p => p.Ci).Aggregate(ResourceAmount.Zero, (sum, p) => sum + p.Requests);
        return new CapacityPlan(nodes, ciUsed, capacity.Scale(options.CiShare));
    }

    /// <summary>Helpers first, then the node with the most memory free.</summary>
    public string? Fits(ResourceAmount request, bool ci)
    {
        if (ci && !_ciLimit.Holds(_ciUsed + request)) return null;
        return _nodes
            .Where(n => n.Free.Holds(request))
            .OrderByDescending(n => n.Helper)
            .ThenByDescending(n => n.Free.MemoryBytes)
            .Select(n => n.Name)
            .FirstOrDefault();
    }

    public void Reserve(string node, ResourceAmount request, bool ci)
    {
        var i = _nodes.FindIndex(n => n.Name == node);
        if (i >= 0) _nodes[i] = _nodes[i] with { Free = _nodes[i].Free - request };
        if (ci) _ciUsed += request;
    }

    private static bool IsHelper(ClusterNode node, (string Key, string Value)? helper) =>
        helper is { } h && node.Labels.TryGetValue(h.Key, out var value) && value == h.Value;

    /// <summary>Ready, schedulable, and every <c>NoSchedule</c>/<c>NoExecute</c> taint is the helper taint step pods tolerate.</summary>
    private static bool Usable(ClusterNode node, (string Key, string Value)? helper) =>
        node.Ready && !node.Unschedulable
        && node.Taints.All(t => t.Effect is not ("NoSchedule" or "NoExecute")
                                || (helper is { } h && t.Key == h.Key && t.Value == h.Value && t.Effect == "NoSchedule"));
}
