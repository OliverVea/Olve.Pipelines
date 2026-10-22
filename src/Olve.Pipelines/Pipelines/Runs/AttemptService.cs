using System.Diagnostics.CodeAnalysis;
using System.Text;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// CRUD, status transitions and lookups over <see cref="Attempt"/>. Singleton: owns indexes (#26)
/// and the creation lock that keeps attempt numbers unique per (run, step).
/// Every transition is a pure <c>Mutate</c> guarded on the current status, so a terminal attempt is
/// never overwritten (e.g. a K8s Job finishing after the attempt was cancelled).
/// </summary>
public class AttemptService(
    EntityStore<Attempt> store,
    EntityStore<Run> runs,
    IdProvider idProvider,
    TimeProvider timeProvider)
{
    /// <summary>Cap on the raw output JSON kept inline on the attempt (and in run state). Outputs
    /// are typed models (refs, shas, URLs); bulk data belongs in files.</summary>
    public const int MaxOutputJsonBytes = 64 * 1024;

    /// <summary>Cap on the assembled <c>input.json</c> kept on the attempt (it is built from outputs,
    /// each capped at <see cref="MaxOutputJsonBytes"/>, plus the run input).</summary>
    public const int MaxInputJsonBytes = 256 * 1024;

    private readonly EntityStoreIndex<Attempt, Id<Pipeline>> _byPipeline = store.CreateIndex(a => a.PipelineId);
    private readonly EntityStoreIndex<Attempt, Id<Run>> _byRun = store.CreateIndex(a => a.RunId);
    private readonly EntityStoreIndex<Attempt, StepKey> _byStep = store.CreateIndex(a => a.Key);
    private readonly Lock _createGate = new();

    /// <summary>
    /// Schedules the next attempt of <paramref name="stepName"/> in a run, carrying its assembled
    /// <c>input.json</c> (<paramref name="input"/>) and that input's canonical hash.
    /// </summary>
    public Result<Attempt> Schedule(Id<Run> runId, string stepName, string? inputHash = null, string? input = null)
    {
        if (input is not null)
        {
            if (Encoding.UTF8.GetByteCount(input) > MaxInputJsonBytes)
                return new ResultProblem("Input of step '{0}' exceeds {1} bytes.", stepName, MaxInputJsonBytes);
            if (!JsonText.IsWellFormed(input))
                return new ResultProblem("Input of step '{0}' is not well-formed JSON.", stepName);
        }

        return Create(runId, stepName, inputHash, _ => new Scheduled(), input);
    }

    /// <summary>Records an attempt the scheduler failed without running (e.g. its input exceeds the cap).</summary>
    public Result<Attempt> RecordFailed(Id<Run> runId, string stepName, string reason, string? inputHash = null)
        => Create(runId, stepName, inputHash, now => new Failed(now, now, reason));

    /// <summary>
    /// Records an attempt the scheduler decided to skip without running (<c>when</c> false, upstream
    /// skipped). Created directly in <see cref="Skipped"/> so the runner never sees it.
    /// </summary>
    public Result<Attempt> RecordSkipped(Id<Run> runId, string stepName, string reason, string? inputHash = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new ResultProblem("A skip reason is required.");

        return Create(runId, stepName, inputHash, now => new Skipped(reason, null, now));
    }

    public bool TryGet(Id<Attempt> id, [NotNullWhen(true)] out Attempt? attempt) => store.TryGet(id, out attempt);

    public IReadOnlyList<Attempt> List() => store.List();

    /// <summary>Every attempt of a pipeline (any status, unordered).</summary>
    public IReadOnlyList<Attempt> ListByPipeline(Id<Pipeline> pipelineId) => Resolve(_byPipeline.GetForKey(pipelineId)).ToList();

    /// <summary>Every attempt of a run, ordered by step name then attempt number.</summary>
    public IReadOnlyList<Attempt> ListByRun(Id<Run> runId)
        => Resolve(_byRun.GetForKey(runId))
            .OrderBy(a => a.StepName, StringComparer.Ordinal)
            .ThenBy(a => a.Number)
            .ToList();

    /// <summary>The attempts of one step in one run, oldest first.</summary>
    public IReadOnlyList<Attempt> ListForStep(Id<Run> runId, string stepName)
        => Resolve(_byRun.GetForKey(runId)).Where(a => a.StepName == stepName).OrderBy(a => a.Number).ToList();

    /// <summary>Every attempt of a step identity across runs (history), newest first.</summary>
    public IReadOnlyList<Attempt> ListByStepName(Id<Pipeline> pipelineId, string stepName)
        => Resolve(_byStep.GetForKey(new StepKey(pipelineId, stepName)))
            .OrderByDescending(a => a.RunNumber)
            .ThenByDescending(a => a.Number)
            .ToList();

    public bool TryGetByNumber(Id<Run> runId, string stepName, int number, [NotNullWhen(true)] out Attempt? attempt)
    {
        attempt = Resolve(_byRun.GetForKey(runId)).FirstOrDefault(a => a.StepName == stepName && a.Number == number);
        return attempt is not null;
    }

    /// <summary>
    /// The latest successful attempt of a step in a run — what later stages consume and what an
    /// address without <c>@attempt</c> resolves to (spec §5.5).
    /// </summary>
    public bool TryGetLatestSucceeded(Id<Run> runId, string stepName, [NotNullWhen(true)] out Attempt? attempt)
    {
        attempt = Resolve(_byRun.GetForKey(runId))
            .Where(a => a.StepName == stepName && a.Status is Succeeded)
            .MaxBy(a => a.Number);
        return attempt is not null;
    }

    /// <summary>
    /// True while the pipeline has any non-terminal attempt. The reconcile drain waits for this to go
    /// false (quiescence, not success).
    /// </summary>
    public bool HasActiveAttempts(Id<Pipeline> pipelineId)
        => Resolve(_byPipeline.GetForKey(pipelineId)).Any(a => a.Status.IsActive());

    /// <summary>True while any attempt of a run is non-terminal.</summary>
    public bool HasActiveAttempts(Id<Run> runId)
        => Resolve(_byRun.GetForKey(runId)).Any(a => a.Status.IsActive());

    /// <summary>The number the next attempt of a step in a run gets.</summary>
    public int PeekNextNumber(Id<Run> runId, string stepName) => MaxNumber(runId, stepName) + 1;

    public Result Update(Id<Attempt> id, Func<Attempt, Attempt> update)
    {
        if (!store.Contains(id))
            return new NotFoundProblem("Attempt '{0}' not found.", id);

        return store.Mutate(id, update);
    }

    /// <summary>Scheduled → InProgress. Returns the start time (the existing one if already InProgress).</summary>
    public Result<DateTimeOffset> MarkInProgress(Id<Attempt> id)
    {
        var now = timeProvider.GetUtcNow();
        if (Update(id, a => a.Status is Scheduled ? a with { Status = new InProgress(now) } : a).TryPickProblems(out var problems))
            return problems;

        return store.TryGet(id, out var after) && after.Status is InProgress ip
            ? ip.StartedAt
            : new ConflictProblem("Attempt '{0}' is not in progress.", id);
    }

    /// <summary>
    /// Scheduled/InProgress → Succeeded, recording the output (if any) under its storage key.
    /// Rejects an output over <see cref="MaxOutputJsonBytes"/> or that is not well-formed JSON.
    /// </summary>
    public Result MarkSucceeded(Id<Attempt> id, string? outputJson = null)
    {
        if (outputJson is not null)
        {
            if (Encoding.UTF8.GetByteCount(outputJson) > MaxOutputJsonBytes)
                return new ResultProblem("Output of attempt '{0}' exceeds {1} bytes.", id, MaxOutputJsonBytes);
            if (!JsonText.IsWellFormed(outputJson))
                return new ResultProblem("Output of attempt '{0}' is not well-formed JSON.", id);
        }

        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status switch
        {
            Scheduled or InProgress => a with
            {
                Status = new Succeeded(StartedAtOr(a, now), now),
                Output = outputJson is null ? null : new AttemptOutput(StorageLayout.AttemptOutputKey(a), outputJson),
            },
            _ => a,
        });
    }

    /// <summary>Scheduled/InProgress → Failed.</summary>
    public Result MarkFailed(Id<Attempt> id, string? reason)
    {
        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status.IsActive() ? a with { Status = new Failed(StartedAtOr(a, now), now, reason) } : a);
    }

    /// <summary>Scheduled/InProgress → Skipped (e.g. the handler returned a skipped envelope).</summary>
    public Result MarkSkipped(Id<Attempt> id, string reason)
    {
        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status.IsActive() ? a with { Status = new Skipped(reason, a.StartedAt(), now) } : a);
    }

    /// <summary>Records one more failed K8s submission on a still-Scheduled attempt; returns the new count.</summary>
    public Result<int> RecordSubmissionFailure(Id<Attempt> id)
    {
        if (Update(id, a => a.Status is Scheduled s ? a with { Status = s with { SubmissionAttempts = s.SubmissionAttempts + 1 } } : a)
            .TryPickProblems(out var problems))
            return problems;

        return store.TryGet(id, out var after) && after.Status is Scheduled scheduled
            ? scheduled.SubmissionAttempts
            : new ConflictProblem("Attempt '{0}' is no longer scheduled.", id);
    }

    public Result Cancel(Id<Attempt> id)
    {
        if (!store.TryGet(id, out var attempt))
            return new NotFoundProblem("Attempt '{0}' not found.", id);

        // Advisory pre-check for the message; the guarded Mutate is the atomic write.
        if (!attempt.Status.IsActive())
            return new ConflictProblem("Attempt '{0}' cannot be cancelled because it is {1}.", id, attempt.Status.Discriminator());

        var now = timeProvider.GetUtcNow();
        return store.Mutate(id, a => a.Status switch
        {
            Scheduled => a with { Status = new Cancelled(null, now) },
            InProgress ip => a with { Status = new Cancelled(ip.StartedAt, now) },
            _ => a,
        });
    }

    /// <summary>Deletes one attempt. Used by run cleanup only: attempts are history, and deleting the
    /// latest one would let its number (and storage prefix) be reused.</summary>
    internal DeletionResult Delete(Id<Attempt> id) => store.Delete(id);

    private Result<Attempt> Create(Id<Run> runId, string stepName, string? inputHash, Func<DateTimeOffset, AttemptStatus> status, string? input = null)
    {
        if (string.IsNullOrWhiteSpace(stepName))
            return new ResultProblem("Step name is required.");
        if (!runs.TryGet(runId, out var run))
            return new NotFoundProblem("Run '{0}' not found.", runId);

        lock (_createGate)
        {
            var now = timeProvider.GetUtcNow();
            var attempt = new Attempt(
                idProvider.Create<Attempt>(),
                run.PipelineId,
                run.Id,
                run.Number,
                stepName,
                MaxNumber(runId, stepName) + 1,
                status(now),
                now,
                inputHash,
                Input: input);
            store.Set(attempt);
            return attempt;
        }
    }

    private static DateTimeOffset StartedAtOr(Attempt attempt, DateTimeOffset fallback) => attempt.StartedAt() ?? fallback;

    private int MaxNumber(Id<Run> runId, string stepName)
        => Resolve(_byRun.GetForKey(runId)).Where(a => a.StepName == stepName).Select(a => a.Number).DefaultIfEmpty(0).Max();

    private IEnumerable<Attempt> Resolve(IEnumerable<Id<Attempt>> ids)
    {
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var attempt))
                yield return attempt;
        }
    }
}
