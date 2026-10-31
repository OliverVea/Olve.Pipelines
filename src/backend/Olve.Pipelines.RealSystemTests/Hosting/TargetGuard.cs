namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>Decides whether a target may be touched. Everything else in the tests goes through a guarded target.</summary>
public static class TargetGuard
{
    public const string MarkerSecret = "olve-real-system-marker";
    public const string MarkerKey = "cluster-id";

    /// <summary>Why the descriptor alone is refused, before anything is contacted; null when it may be probed.</summary>
    public static string? RefuseDescriptor(Uri apiUrl, Uri kubernetesServer, string clusterId)
    {
        if (apiUrl.Host.EndsWith(".ovea.pro", StringComparison.OrdinalIgnoreCase))
            return $"Refusing {apiUrl}: it is a real instance, not a test host.";
        if (string.IsNullOrWhiteSpace(clusterId))
            return $"Refusing {apiUrl}: its descriptor names no test cluster id.";
        if (kubernetesServer.Port == 6443)
            return $"Refusing {apiUrl}: port 6443 of {kubernetesServer.Host} is where a real cluster listens; test clusters use another port.";
        return null;
    }

    /// <summary>Why the cluster is refused, given the marker read from it (null when it has none).</summary>
    public static string? RefuseCluster(Uri kubernetesServer, string clusterId, string? markerClusterId)
    {
        if (markerClusterId is null)
            return $"Refusing the cluster at {kubernetesServer}: it has no {MarkerSecret} Secret, so it is not a test cluster.";
        if (markerClusterId != clusterId)
            return $"Refusing the cluster at {kubernetesServer}: it is test cluster '{markerClusterId}', not '{clusterId}' as the descriptor says.";
        return null;
    }
}
