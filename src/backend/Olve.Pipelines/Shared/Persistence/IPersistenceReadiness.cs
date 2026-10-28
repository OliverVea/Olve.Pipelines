using System.Collections.Concurrent;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Aggregates the load state of the snapshot persistence services. Each persistence service
/// registers a gate at startup and flips it ready once its load is confirmed (first run or a
/// successful load). The readiness endpoint reports ready only when every gate is ready, so
/// Kubernetes never routes traffic to a pod that would serve empty state.
/// </summary>
public interface IPersistenceReadiness
{
    /// <summary>Declares a gate that must be marked ready before the app is considered ready.</summary>
    void Register(string name);

    /// <summary>Marks a previously-registered gate as ready.</summary>
    void MarkReady(string name);

    /// <summary>True only when at least one gate is registered and all gates are ready.</summary>
    bool IsReady { get; }
}

public sealed class PersistenceReadiness : IPersistenceReadiness
{
    private readonly ConcurrentDictionary<string, bool> _gates = new();

    public void Register(string name) => _gates.TryAdd(name, false);

    public void MarkReady(string name) => _gates[name] = true;

    public bool IsReady => !_gates.IsEmpty && _gates.Values.All(ready => ready);
}
