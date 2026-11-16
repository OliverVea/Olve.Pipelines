using System.Diagnostics.CodeAnalysis;
using System.Text;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>What a new CI run tests and runs with.</summary>
public sealed record CiRunRequest(
    Id<Pipeline> PipelineId,
    int Pull,
    string Title,
    string Branch,
    bool Draft,
    string HeadSha,
    string TestedSha,
    string Tree,
    string MainSha,
    PipelineVersion Version,
    string Input,
    int? ConfigSnapshot,
    string? Config,
    IReadOnlyList<string>? Problems = null,
    bool OwnDefinition = false,
    CiRunKind Kind = CiRunKind.Pull,
    IReadOnlyList<CiBatchPull>? Batch = null,
    int? Number = null);

/// <summary>CI runs: their numbers (a sequence per pipeline of their own), lookups and status. Singleton: owns indexes and the numbering lock.</summary>
public class CiRunService(EntityStore<CiRun> store, IdProvider idProvider, TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<CiRun, Id<Pipeline>> _byPipeline = store.CreateIndex(r => r.PipelineId);
    private readonly EntityStoreIndex<CiRun, (Id<Pipeline>, int)> _byPull = store.CreateIndex(r => (r.PipelineId, r.Pull));
    private readonly EntityStoreUniqueIndex<CiRun, Id<CiRun>, (Id<Pipeline>, int)> _byNumber =
        store.CreateUniqueIndex<CiRun, Id<CiRun>, (Id<Pipeline>, int)>(r => (r.PipelineId, r.Number));
    private readonly Lock _createGate = new();
    private readonly Dictionary<Id<Pipeline>, int> _reserved = [];

    /// <summary>The next run number, held for a run created later with it (a queue run names its batch branch after it).</summary>
    public int Reserve(Id<Pipeline> pipelineId)
    {
        lock (_createGate)
        {
            var number = NextNumber(pipelineId);
            _reserved[pipelineId] = number;
            return number;
        }
    }

    private int NextNumber(Id<Pipeline> pipelineId) =>
        Math.Max(ListByPipeline(pipelineId).Select(r => r.Number).DefaultIfEmpty(0).Max(), _reserved.GetValueOrDefault(pipelineId)) + 1;

    public CiRun Create(CiRunRequest request)
    {
        lock (_createGate)
        {
            var number = request.Number ?? NextNumber(request.PipelineId);
            var now = timeProvider.GetUtcNow();
            var failed = request.Problems is { Count: > 0 };
            var run = new CiRun(
                idProvider.Create<CiRun>(),
                request.PipelineId,
                number,
                request.Pull,
                request.Title,
                request.Branch,
                request.Draft,
                request.HeadSha,
                request.TestedSha,
                request.Tree,
                request.MainSha,
                request.Version.Id,
                request.Version.Number,
                request.Input,
                failed ? RunStatus.Failed : RunStatus.Running,
                now,
                FinishedAt: failed ? now : null,
                ConfigSnapshot: request.ConfigSnapshot,
                Config: request.Config,
                Problems: request.Problems,
                OwnDefinition: request.OwnDefinition,
                Building: request.OwnDefinition && !failed && request.Kind != CiRunKind.Close,
                Kind: request.Kind,
                Batch: request.Batch);
            store.Set(run);
            return run;
        }
    }

    public bool TryGet(Id<CiRun> id, [NotNullWhen(true)] out CiRun? run) => store.TryGet(id, out run);

    public bool TryGetByNumber(Id<Pipeline> pipelineId, int number, [NotNullWhen(true)] out CiRun? run)
    {
        run = null;
        return _byNumber.TryGet((pipelineId, number), out var id) && store.TryGet(id, out run);
    }

    public IReadOnlyList<CiRun> List() => store.List();

    public IReadOnlyList<CiRun> ListByPipeline(Id<Pipeline> pipelineId) => Resolve(_byPipeline.GetForKey(pipelineId));

    /// <summary>A pull request's runs that test it, oldest first: close runs are left out.</summary>
    public IReadOnlyList<CiRun> ListByPull(Id<Pipeline> pipelineId, int pull) =>
        [.. Resolve(_byPull.GetForKey((pipelineId, pull))).Where(r => !r.IsClose).OrderBy(r => r.Number)];

    /// <summary>A pull request's close runs, oldest first.</summary>
    public IReadOnlyList<CiRun> ListCloseRuns(Id<Pipeline> pipelineId, int pull) =>
        [.. Resolve(_byPull.GetForKey((pipelineId, pull))).Where(r => r.IsClose).OrderBy(r => r.Number)];

    /// <summary>Running → a terminal status (no-op when already terminal).</summary>
    public Result<CiRun> Complete(Id<CiRun> id, RunStatus status, CiCancelReason? reason = null, int? supersededBy = null)
    {
        if (!store.Contains(id))
            return new NotFoundProblem("CI run '{0}' not found.", id);
        var now = timeProvider.GetUtcNow();
        if (store.Mutate(id, r => r.Status is RunStatus.Running
                ? r with { Status = status, FinishedAt = now, CancelReason = reason ?? r.CancelReason, SupersededByNumber = supersededBy ?? r.SupersededByNumber }
                : r).TryPickProblems(out var problems))
            return problems;
        return store.TryGet(id, out var after) ? after : new NotFoundProblem("CI run '{0}' not found.", id);
    }

    /// <summary>A run that waited for its own definition: the config it runs with is pinned, its steps may start.</summary>
    public Result<CiRun> Define(Id<CiRun> id, int? configSnapshot, string? config) =>
        Mutate(id, r => r.Building ? r with { Building = false, ConfigSnapshot = configSnapshot, Config = config } : r);

    /// <summary>A running run whose definition can't be used fails before any step ran, saying why.</summary>
    public Result<CiRun> FailDefinition(Id<CiRun> id, IReadOnlyList<string> problems)
    {
        var now = timeProvider.GetUtcNow();
        return Mutate(id, r => r.Status is RunStatus.Running
            ? r with { Status = RunStatus.Failed, FinishedAt = now, Problems = problems, Building = false }
            : r);
    }

    public DeletionResult Delete(Id<CiRun> id) => store.Delete(id);

    private Result<CiRun> Mutate(Id<CiRun> id, Func<CiRun, CiRun> change)
    {
        if (store.Mutate(id, change).TryPickProblems(out var problems))
            return problems;
        return store.TryGet(id, out var after) ? after : new NotFoundProblem("CI run '{0}' not found.", id);
    }

    private List<CiRun> Resolve(IEnumerable<Id<CiRun>> ids) =>
        [.. ids.Select(id => store.TryGet(id, out var r) ? r : null).OfType<CiRun>()];
}

/// <summary>
/// CI attempts: creation (numbers unique per run and step), lookups and status transitions, each guarded on the
/// current status so a terminal attempt is never overwritten. Singleton: owns indexes and the creation lock.
/// </summary>
public class CiAttemptService(EntityStore<CiAttempt> store, EntityStore<CiRun> runs, IdProvider idProvider, TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<CiAttempt, Id<CiRun>> _byRun = store.CreateIndex(a => a.RunId);
    private readonly Lock _createGate = new();

    public Result<CiAttempt> Schedule(Id<CiRun> runId, string stepName, string inputHash, string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > AttemptService.MaxInputJsonBytes)
            return new ResultProblem("Input of step '{0}' exceeds {1} bytes.", stepName, AttemptService.MaxInputJsonBytes);
        if (!JsonText.IsWellFormed(input))
            return new ResultProblem("Input of step '{0}' is not well-formed JSON.", stepName);
        return Create(runId, stepName, _ => new Scheduled(), inputHash, input);
    }

    public Result<CiAttempt> RecordSkipped(Id<CiRun> runId, string stepName, string reason) =>
        Create(runId, stepName, now => new Skipped(reason, null, now));

    /// <summary>Records a step as succeeded with <paramref name="origin"/>'s output: no Job runs.</summary>
    public Result<CiAttempt> RecordReused(Id<CiRun> runId, string stepName, string inputHash, string input, CiAttempt origin) =>
        Create(runId, stepName, now => new Succeeded(now, now), inputHash, input, a => a with
        {
            Output = origin.Output,
            Links = origin.Links,
            ReusedFrom = new CiReuse(origin.Id, origin.RunNumber, origin.StepName, origin.Number),
        });

    /// <summary>The attempt that ran for <paramref name="attempt"/>: itself, or the one it reuses (null when that is gone).</summary>
    public CiAttempt? Origin(CiAttempt attempt) =>
        attempt.ReusedFrom is { } reuse ? (store.TryGet(reuse.AttemptId, out var origin) ? origin : null) : attempt;

    public Result<CiAttempt> RecordFailed(Id<CiRun> runId, string stepName, string reason, string? inputHash = null) =>
        Create(runId, stepName, now => new Failed(now, now, reason, AttemptFailureKind.NotRun), inputHash);

    public bool TryGet(Id<CiAttempt> id, [NotNullWhen(true)] out CiAttempt? attempt) => store.TryGet(id, out attempt);

    public IReadOnlyList<CiAttempt> List() => store.List();

    /// <summary>A run's attempts in creation order.</summary>
    public IReadOnlyList<CiAttempt> ListByRun(Id<CiRun> runId) =>
        [.. _byRun.GetForKey(runId).Select(id => store.TryGet(id, out var a) ? a : null).OfType<CiAttempt>().OrderBy(a => a.CreatedAt).ThenBy(a => a.Number)];

    /// <summary>A step's (or cell's) attempts in a run, by number.</summary>
    public IReadOnlyList<CiAttempt> ListForStep(Id<CiRun> runId, string stepName) =>
        [.. ListByRun(runId).Where(a => a.StepName == stepName).OrderBy(a => a.Number)];

    public bool TryGetLatestSucceeded(Id<CiRun> runId, string stepName, [NotNullWhen(true)] out CiAttempt? attempt)
    {
        attempt = ListForStep(runId, stepName).LastOrDefault(a => a.Status is Succeeded);
        return attempt is not null;
    }

    public Result Update(Id<CiAttempt> id, Func<CiAttempt, CiAttempt> update) =>
        store.Contains(id) ? store.Mutate(id, update) : new NotFoundProblem("CI attempt '{0}' not found.", id);

    public Result MarkInProgress(Id<CiAttempt> id)
    {
        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status is Scheduled ? a with { Status = new InProgress(now) } : a);
    }

    public Result MarkSucceeded(Id<CiAttempt> id, string outputKey, string? outputJson)
    {
        if (outputJson is not null)
        {
            if (Encoding.UTF8.GetByteCount(outputJson) > AttemptService.MaxOutputJsonBytes)
                return new ResultProblem("Output of attempt '{0}' exceeds {1} bytes.", id, AttemptService.MaxOutputJsonBytes);
            if (!JsonText.IsWellFormed(outputJson))
                return new ResultProblem("Output of attempt '{0}' is not well-formed JSON.", id);
        }

        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status is Scheduled or InProgress
            ? a with { Status = new Succeeded(a.StartedAt() ?? now, now), Output = outputJson is null ? null : new AttemptOutput(outputKey, outputJson) }
            : a);
    }

    public Result MarkFailed(Id<CiAttempt> id, string? reason, AttemptFailureKind? kind = null)
    {
        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status.IsActive() ? a with { Status = new Failed(a.StartedAt() ?? now, now, reason, kind) } : a);
    }

    public Result MarkSkipped(Id<CiAttempt> id, string reason)
    {
        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status.IsActive() ? a with { Status = new Skipped(reason, a.StartedAt(), now) } : a);
    }

    public Result RecordSubmissionFailure(Id<CiAttempt> id) =>
        Update(id, a => a.Status is Scheduled s ? a with { Status = s with { SubmissionAttempts = s.SubmissionAttempts + 1 } } : a);

    /// <summary>Scheduled/InProgress → Cancelled; a no-op on a terminal attempt.</summary>
    public Result Cancel(Id<CiAttempt> id, string? reason = null)
    {
        var now = timeProvider.GetUtcNow();
        return Update(id, a => a.Status switch
        {
            Waiting or Scheduled => a with { Status = new Cancelled(null, now, reason) },
            InProgress ip => a with { Status = new Cancelled(ip.StartedAt, now, reason) },
            _ => a,
        });
    }

    public DeletionResult Delete(Id<CiAttempt> id) => store.Delete(id);

    private Result<CiAttempt> Create(Id<CiRun> runId, string stepName, Func<DateTimeOffset, AttemptStatus> status, string? inputHash = null, string? input = null,
        Func<CiAttempt, CiAttempt>? complete = null)
    {
        if (!runs.TryGet(runId, out var run))
            return new NotFoundProblem("CI run '{0}' not found.", runId);

        lock (_createGate)
        {
            var now = timeProvider.GetUtcNow();
            var number = ListForStep(runId, stepName).Select(a => a.Number).DefaultIfEmpty(0).Max() + 1;
            var attempt = new CiAttempt(idProvider.Create<CiAttempt>(), run.PipelineId, run.Id, run.Number, stepName, number, status(now), now,
                inputHash, Input: input);
            if (complete is not null)
                attempt = complete(attempt);
            store.Set(attempt);
            return attempt;
        }
    }
}
