namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Shared reconcile concurrency state (singleton). Holds the set of pipelines currently being
/// reconciled (paused) and the single coarse global config-mutation lock.
///
/// While a pipeline is paused the trigger layer refuses to START new production runs; in-flight
/// chains are unaffected and run to completion on the config they started with. The mutation lock
/// is held only around the synchronous diff-apply — never across the drain wait or any I/O.
/// </summary>
public sealed class ReconcilePauseState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Id<Pipeline>> _paused = [];

    /// <summary>The one coarse lock guarding the synchronous config mutate.</summary>
    public Lock MutationLock { get; } = new();

    public void Pause(Id<Pipeline> pipelineId)
    {
        lock (_gate)
            _paused.Add(pipelineId);
    }

    public void Resume(Id<Pipeline> pipelineId)
    {
        lock (_gate)
            _paused.Remove(pipelineId);
    }

    public bool IsPaused(Id<Pipeline> pipelineId)
    {
        lock (_gate)
            return _paused.Contains(pipelineId);
    }
}
