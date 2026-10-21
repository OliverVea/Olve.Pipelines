using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Promotion gates of a pipeline (spec §5.3): the blocked step names. Operational state, not config —
/// keyed on step name (identity), so a braked step stays braked across version switches. Attached to
/// the pipeline (absence = nothing blocked) and persisted in <c>pipeline-state.json</c>.
/// </summary>
public sealed record PipelineStepGates(IReadOnlyList<string> BlockedSteps)
{
    public bool IsBlocked(string stepName) => BlockedSteps.Contains(stepName, StringComparer.Ordinal);
}

/// <summary>
/// A run start requested while the pipeline was draining (spec §6.2): queued latest-wins per pipeline,
/// started on the new active version when the switch completes. Persisted in <c>pipeline-state.json</c>.
/// </summary>
public sealed record PendingRunStart(string Input, RunTrigger Trigger, DateTimeOffset QueuedAt, Id<Run>? RerunOf = null);

/// <summary>
/// The single scheduling lock (spec §5.3). Every entry point that creates runs or attempts, or moves a
/// run between states, takes it first — so sibling attempts finishing on different threads cannot both
/// pass a stage barrier, and the creation locks inside <see cref="RunService"/>/<see cref="AttemptService"/>
/// are always taken after it (no lock-order inversion). <see cref="Lock"/> is re-entrant: synchronous
/// store events fired under it re-enter the scheduler on the same thread.
/// It also tracks which runs are being advanced, so a nested advance of the same run (from an event
/// fired by that run's own writes) is deferred to the outer loop instead of racing it. Singleton.
/// </summary>
public sealed class RunSchedulingLock
{
    private readonly Lock _lock = new();
    private readonly HashSet<Id<Run>> _advancing = [];
    private readonly HashSet<Id<Run>> _dirty = [];
    private const int MaxPasses = 64;

    public void Run(Action action)
    {
        lock (_lock)
        {
            action();
        }
    }

    public T Run<T>(Func<T> action)
    {
        lock (_lock)
        {
            return action();
        }
    }

    /// <summary>
    /// Runs <paramref name="advance"/> for a run, re-running it while nested requests for the same run
    /// arrived during it. A nested request returns immediately (the outer loop picks it up).
    /// </summary>
    public void Advance(Id<Run> runId, Action advance)
    {
        lock (_lock)
        {
            if (!_advancing.Add(runId))
            {
                _dirty.Add(runId);
                return;
            }

            try
            {
                // Bounded: each re-run is caused by a write of the previous pass; a pass that finds
                // nothing to do writes nothing, so this converges in a few passes.
                var passes = 0;
                do
                {
                    _dirty.Remove(runId);
                    advance();
                } while (_dirty.Contains(runId) && ++passes < MaxPasses);
            }
            finally
            {
                _advancing.Remove(runId);
                _dirty.Remove(runId);
            }
        }
    }
}
