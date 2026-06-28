using System.Collections.Concurrent;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// Fire-once guard for job-group completion. A group becomes fully terminal exactly once in its
/// lifetime (terminal statuses are final and monotonic), but the last N jobs of a group can reach a
/// terminal state on separate watcher threads concurrently — each then observes the whole group
/// terminal. <see cref="TryClaimCompletion"/> is an atomic compare-and-set that lets exactly one of
/// those threads proceed to raise <see cref="JobEvents.OnGroupCompleted"/> / <see cref="JobEvents.OnGroupFailed"/>.
///
/// Without it, a multi-step production group double-fires completion and promotes its bundle into the
/// first processing step twice, creating two mutually-superseding processing jobs that silently
/// deadlock the pipeline. Singleton: the claim set is shared state across the transient completion
/// service instances resolved per dispatch.
/// </summary>
public class JobGroupCompletionTracker
{
    private readonly ConcurrentDictionary<Id<JobGroup>, byte> _claimed = new();

    /// <summary>
    /// Returns <c>true</c> for the first caller per <paramref name="groupId"/> and <c>false</c> for
    /// every caller after, regardless of how many observe the group terminal concurrently.
    /// </summary>
    public bool TryClaimCompletion(Id<JobGroup> groupId) => _claimed.TryAdd(groupId, 0);
}
