using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// Logs of an attempt that is still running (its pod's current output). A seam: the K8s
/// implementation is registered only when Kubernetes is configured; otherwise
/// <see cref="NoAttemptLiveLogs"/> reports nothing.
/// </summary>
public interface IAttemptLiveLogs
{
    /// <summary>The pod's current log, or null when there is none yet (pod not started, init still running).</summary>
    Task<string?> TryGetAsync(Attempt attempt, CancellationToken ct);
}

public sealed class NoAttemptLiveLogs : IAttemptLiveLogs
{
    public Task<string?> TryGetAsync(Attempt attempt, CancellationToken ct) => Task.FromResult<string?>(null);
}
