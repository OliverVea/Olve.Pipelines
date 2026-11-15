using Olve.Pipelines.Kubernetes;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// The latest <see cref="ClusterView"/> (read at most every <see cref="ReadInterval"/>) plus the requests of pods
/// dispatch started since, which take a moment to appear in a read. Null <see cref="Plan"/> = the cluster could not
/// be read, and dispatch falls back to counting Jobs. Singleton.
/// </summary>
public sealed class ClusterCapacityTracker(IKubernetesCapacity source, CapacityOptions options, TimeProvider time, ILogger<ClusterCapacityTracker> logger)
{
    public static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(5);

    /// <summary>A started pod's request counts on its own until the Job controller has created the pod and a read sees it.</summary>
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly List<(DateTimeOffset At, ResourceAmount Request, bool Ci)> _recent = [];
    private ClusterView? _view;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private bool _failing;

    public CapacityOptions Options => options;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() - _readAt < ReadInterval) return;
        try
        {
            var view = await source.ReadClusterAsync(ct);
            lock (_lock)
            {
                _view = view;
                _readAt = time.GetUtcNow();
            }
            if (_failing) logger.LogInformation("Cluster capacity readable again: dispatching by fit");
            _failing = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                _view = null;
                _readAt = time.GetUtcNow();
            }
            if (!_failing) logger.LogWarning(ex, "Cannot read the cluster's capacity: dispatching by Job count until it can");
            _failing = true;
        }
    }

    /// <summary>A plan over the latest view and this tracker's recent reservations; null while the cluster can't be read.</summary>
    public TrackedPlan? Plan()
    {
        lock (_lock)
        {
            if (_view is null) return null;
            var now = time.GetUtcNow();
            _recent.RemoveAll(r => now - r.At > ReservationLifetime);
            var pods = _view.Pods.Concat(_recent.Select(r => new ClusterPod(null, r.Request, r.Ci))).ToList();
            return new TrackedPlan(CapacityPlan.Create(_view with { Pods = pods }, options), this);
        }
    }

    private void Record(ResourceAmount request, bool ci)
    {
        lock (_lock) _recent.Add((time.GetUtcNow(), request, ci));
    }

    /// <summary>A <see cref="CapacityPlan"/> whose reservations the tracker remembers for later ticks.</summary>
    public sealed class TrackedPlan(CapacityPlan plan, ClusterCapacityTracker tracker) : ICapacityPlan
    {
        public string? Fits(ResourceAmount request, bool ci) => plan.Fits(request, ci);

        public void Reserve(string node, ResourceAmount request, bool ci)
        {
            plan.Reserve(node, request, ci);
            tracker.Record(request, ci);
        }
    }
}
