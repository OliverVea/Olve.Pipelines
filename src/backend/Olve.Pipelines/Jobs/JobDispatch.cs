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
/// </summary>
public sealed record JobCandidate(JobPriority Priority, DateTimeOffset CreatedAt, Func<bool> TryStart, Func<bool>? KindSlotFree = null);

/// <summary>Hands out the Job slots, which every kind of attempt shares (<c>Jobs:MaxConcurrent</c>).</summary>
public static class JobDispatch
{
    /// <summary>Offers each candidate a slot, by priority and then oldest first, until none is free.</summary>
    /// <returns>How many candidates started.</returns>
    public static int Run(IEnumerable<JobCandidate> candidates, Func<bool> slotFree)
    {
        var started = 0;
        foreach (var candidate in candidates.OrderBy(c => c.Priority).ThenBy(c => c.CreatedAt))
        {
            if (!slotFree()) break;
            if (candidate.KindSlotFree is { } kindSlotFree && !kindSlotFree()) continue;
            if (candidate.TryStart()) started++;
        }
        return started;
    }
}
