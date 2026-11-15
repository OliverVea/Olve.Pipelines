namespace Olve.Pipelines.Jobs;

/// <summary>Which work gets a free Job slot first (docs/design/ci.md, Priority): lower goes first.</summary>
public enum JobPriority
{
    /// <summary>CD runs: deploys never wait behind checks.</summary>
    Deploy = 0,

    /// <summary>Merge-queue runs: they land changes.</summary>
    MergeQueue = 1,

    /// <summary>Runs of pull requests ready for review.</summary>
    ReadyPull = 2,

    /// <summary>Runs of pull requests whose result was checked by a definition the active version has replaced.</summary>
    OutdatedPull = 3,

    /// <summary>Runs of draft pull requests.</summary>
    DraftPull = 4,
}

/// <summary>
/// An attempt that wants its watcher started. <see cref="TryStart"/> applies its own kind's rules (CD's one attempt
/// per step, a matrix step's <c>maxParallel</c>) and starts the watcher; false when those rules hold it back.
/// <see cref="KindSlotFree"/> is its kind's own limit within the shared slots (CI's <c>Jobs:MaxConcurrentCi</c>).
/// <see cref="Request"/> is what the pod it would create requests; null when it reattaches to one that exists.
/// </summary>
public sealed record JobCandidate(
    JobPriority Priority,
    DateTimeOffset CreatedAt,
    Func<bool> TryStart,
    Func<bool>? KindSlotFree = null,
    ResourceAmount? Request = null,
    bool Ci = false);

/// <summary>
/// Hands out room for step pods: a candidate starts when its request fits on a node it may use (<paramref name="capacity"/>),
/// within the Job ceiling (<c>Jobs:MaxConcurrent</c>) and its kind's.
/// </summary>
public static class JobDispatch
{
    /// <summary>Offers each candidate room, by priority and then oldest first. Without a capacity plan only the counts apply.</summary>
    /// <returns>How many candidates started.</returns>
    public static int Run(IEnumerable<JobCandidate> candidates, Func<bool> slotFree, ICapacityPlan? capacity = null)
    {
        var started = 0;
        foreach (var candidate in candidates.OrderBy(c => c.Priority).ThenBy(c => c.CreatedAt))
        {
            if (!slotFree()) break;
            if (candidate.KindSlotFree is { } kindSlotFree && !kindSlotFree()) continue;
            string? node = null;
            if (capacity is not null && candidate.Request is { } request && (node = capacity.Fits(request, candidate.Ci)) is null)
                continue;
            if (!candidate.TryStart()) continue;
            started++;
            if (node is not null) capacity!.Reserve(node, candidate.Request!.Value, candidate.Ci);
        }
        return started;
    }
}
