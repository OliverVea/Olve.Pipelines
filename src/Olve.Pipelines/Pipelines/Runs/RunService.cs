using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// CRUD, status transitions and per-pipeline lookups over <see cref="Run"/>. Singleton: owns indexes
/// (#26) and the creation lock that keeps run numbers unique per pipeline.
/// </summary>
public class RunService(
    EntityStore<Run> store,
    EntityStore<PipelineVersion> versions,
    PipelineNumberingService numbering,
    IdProvider idProvider,
    TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<Run, Id<Pipeline>> _byPipeline = store.CreateIndex(r => r.PipelineId);
    private readonly EntityStoreUniqueIndex<Run, Id<Run>, (Id<Pipeline>, int)> _byNumber =
        store.CreateUniqueIndex<Run, Id<Run>, (Id<Pipeline>, int)>(r => (r.PipelineId, r.Number));
    private readonly Lock _createGate = new();

    /// <summary>
    /// Creates the next run of a pipeline, pinned to <paramref name="versionId"/>, in
    /// <see cref="RunStatus.Running"/>. Input validation against the input model is the trigger
    /// layer's job (B7); here it only has to be well-formed JSON.
    /// </summary>
    public Result<Run> Create(
        Id<Pipeline> pipelineId,
        Id<PipelineVersion> versionId,
        string input,
        RunTrigger trigger,
        Id<Run>? rerunOf = null)
    {
        if (!versions.TryGet(versionId, out var version) || version.PipelineId != pipelineId)
            return new ResultProblem("Version '{0}' does not belong to pipeline '{1}'.", versionId, pipelineId);
        if (!JsonText.IsWellFormed(input))
            return new ResultProblem("Run input must be well-formed JSON.");
        if (rerunOf is { } original && (!store.TryGet(original, out var originalRun) || originalRun.PipelineId != pipelineId))
            return new NotFoundProblem("Run '{0}' to re-run was not found in pipeline '{1}'.", original, pipelineId);

        lock (_createGate)
        {
            var number = numbering.AllocateRunNumber(pipelineId, MaxNumber(pipelineId));
            var now = timeProvider.GetUtcNow();
            var run = new Run(
                idProvider.Create<Run>(),
                pipelineId,
                number,
                version.Id,
                version.Number,
                input,
                RunVersionStamp.Format(now, number),
                RunStatus.Running,
                trigger,
                now,
                RerunOf: rerunOf);
            store.Set(run);
            return run;
        }
    }

    public bool TryGet(Id<Run> id, [NotNullWhen(true)] out Run? run) => store.TryGet(id, out run);

    public bool TryGetByNumber(Id<Pipeline> pipelineId, int number, [NotNullWhen(true)] out Run? run)
    {
        run = null;
        return _byNumber.TryGet((pipelineId, number), out var id) && store.TryGet(id, out run);
    }

    /// <summary>Resolves a version stamp (<c>yyyy.MM.dd.&lt;n&gt;</c>) — "version X" and "build n" are the same run.</summary>
    public bool TryGetByVersionStamp(Id<Pipeline> pipelineId, string stamp, [NotNullWhen(true)] out Run? run)
    {
        run = null;
        if (!RunVersionStamp.TryParseRunNumber(stamp, out var number)
            || !TryGetByNumber(pipelineId, number, out var candidate)
            || candidate.VersionStamp != stamp)
            return false;

        run = candidate;
        return true;
    }

    public bool TryGetLatest(Id<Pipeline> pipelineId, [NotNullWhen(true)] out Run? run)
    {
        run = Resolve(pipelineId).MaxBy(r => r.Number);
        return run is not null;
    }

    /// <summary>All runs of a pipeline, newest first.</summary>
    public IReadOnlyList<Run> ListByPipeline(Id<Pipeline> pipelineId)
        => Resolve(pipelineId).OrderByDescending(r => r.Number).ToList();

    /// <summary>Runs pinned to one version (draining checks, retention).</summary>
    public IReadOnlyList<Run> ListByVersion(PipelineVersion version)
        => Resolve(version.PipelineId).Where(r => r.VersionId == version.Id).ToList();

    /// <summary>The number the next <see cref="Create"/> for this pipeline would allocate.</summary>
    public int PeekNextNumber(Id<Pipeline> pipelineId)
        => numbering.PeekNextRunNumber(pipelineId, MaxNumber(pipelineId));

    /// <summary>Moves a <see cref="RunStatus.Running"/> run to a terminal status (not Superseded — see <see cref="Supersede"/>).</summary>
    public Result<Run> Complete(Id<Run> id, RunStatus status)
    {
        if (status is RunStatus.Running or RunStatus.Superseded)
            return new ResultProblem("'{0}' is not a completion status.", status);

        var now = timeProvider.GetUtcNow();
        return Transition(id, r => r with { Status = status, FinishedAt = now, HeldAt = null });
    }

    public Result<Run> Cancel(Id<Run> id) => Complete(id, RunStatus.Cancelled);

    /// <summary>Marks a running run superseded by a newer automatic run (spec §5.3).</summary>
    public Result<Run> Supersede(Id<Run> id, Id<Run> supersededBy)
    {
        if (id == supersededBy)
            return new ResultProblem("A run cannot supersede itself.");

        var now = timeProvider.GetUtcNow();
        return Transition(id, r => r with { Status = RunStatus.Superseded, SupersededBy = supersededBy, FinishedAt = now, HeldAt = null });
    }

    /// <summary>
    /// Re-opens a finished run for a continue (spec §5.2): Succeeded/Failed/Cancelled → Running, with
    /// <see cref="Run.ResumedAt"/> = now (its failed/cancelled attempts become stale and are retried).
    /// A superseded run cannot be re-opened — a newer run replaced it. Optionally replaces the input.
    /// Also valid on a Running run (only stamps <see cref="Run.ResumedAt"/> and the input).
    /// </summary>
    public Result<Run> Reopen(Id<Run> id, string? input = null)
    {
        if (input is not null && !JsonText.IsWellFormed(input))
            return new ResultProblem("Run input must be well-formed JSON.");
        if (!store.TryGet(id, out var current))
            return new NotFoundProblem("Run '{0}' not found.", id);
        if (current.Status is RunStatus.Superseded)
            return new ConflictProblem("Run #{0} was superseded by a newer run and cannot be continued.", current.Number);

        var now = timeProvider.GetUtcNow();
        var mutated = store.Mutate(id, r => r.Status is RunStatus.Superseded
            ? r
            : r with { Status = RunStatus.Running, FinishedAt = null, HeldAt = null, ResumedAt = now, Input = input ?? r.Input });
        if (mutated.TryPickProblems(out var problems))
            return problems;

        return store.TryGet(id, out var after) && after.Status is RunStatus.Running
            ? after
            : new ConflictProblem("Run '{0}' changed concurrently.", id);
    }

    /// <summary>Sets (or clears, with null) the gate hold marker of a Running run. No-op when unchanged.</summary>
    public Result SetHeldAt(Id<Run> id, string? stepName)
    {
        if (!store.TryGet(id, out var current))
            return new NotFoundProblem("Run '{0}' not found.", id);
        if (current.HeldAt == stepName || current.Status is not RunStatus.Running)
            return Result.Success();

        return store.Mutate(id, r => r.Status is RunStatus.Running ? r with { HeldAt = stepName } : r);
    }

    public DeletionResult Delete(Id<Run> id) => store.Delete(id);

    private Result<Run> Transition(Id<Run> id, Func<Run, Run> fromRunning)
    {
        if (!store.TryGet(id, out var current))
            return new NotFoundProblem("Run '{0}' not found.", id);
        if (current.Status.IsTerminal())
            return new ConflictProblem("Run #{0} is already {1}.", current.Number, current.Status);

        var mutated = store.Mutate(id, r => r.Status.IsTerminal() ? r : fromRunning(r));
        if (mutated.TryPickProblems(out var problems))
            return problems;

        return store.TryGet(id, out var after) && after.Status != RunStatus.Running
            ? after
            : new ConflictProblem("Run '{0}' changed concurrently.", id);
    }

    private int MaxNumber(Id<Pipeline> pipelineId)
        => Resolve(pipelineId).Select(r => r.Number).DefaultIfEmpty(0).Max();

    private IEnumerable<Run> Resolve(Id<Pipeline> pipelineId)
    {
        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (store.TryGet(id, out var run))
                yield return run;
        }
    }
}
