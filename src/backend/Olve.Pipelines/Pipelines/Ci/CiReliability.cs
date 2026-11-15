using Olve.Pipelines.Pipelines.Runs;
using Olve.TypeSpec;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// How reliable a pipeline's CI steps were over its latest CI runs. A step's failure is judged by what happened to the
/// same step (or cell) on the same tree with the same code hash, anywhere in the pipeline's CI history: it passed there
/// (flaky), failed there again and never passed (reproduced), or it never ran there again but a later head of the same
/// pull request passed it (caught). Steps are ranked by flakiness, never by how often they fail. Read-only; transient.
/// </summary>
public class CiReliabilityService(CiRunService runs, CiAttemptService attempts, CiPlanCache plans)
{
    public const int DefaultRuns = 200;
    public const int MaxRuns = 1000;

    public Result<CiReliabilityReport> Get(Id<Pipeline> pipelineId, int? requestedRuns = null)
    {
        var requested = requestedRuns ?? DefaultRuns;
        if (requested is < 1 or > MaxRuns)
            return ValidationProblem.ForField("runs", "runs must be between 1 and {0}, not {1}.", MaxRuns, requested);

        var history = runs.ListByPipeline(pipelineId)
            .Where(r => !r.IsClose)
            .OrderBy(r => r.Number)
            .Select(r => new CiReliabilityRun(
                r.Number,
                r.Tree,
                plans.Get(r).TryPickProblems(out _, out var plan) ? null : plan.Manifest.CodeHash,
                r.IsQueue ? [.. (r.Batch ?? []).Select(b => (b.Number, b.HeadSha))] : [(r.Pull, r.HeadSha)],
                r.IsQueue,
                attempts.ListByRun(r.Id)))
            .ToList();
        return CiReliability.Report(history, requested);
    }
}

/// <summary>A CI run as the report sees it: its tree, the code hash of the definition it used (null: unknown), and the pull requests it tested with their heads.</summary>
public sealed record CiReliabilityRun(
    int Number,
    string Tree,
    string? CodeHash,
    IReadOnlyList<(int Pull, string Head)> Pulls,
    bool Queue,
    IReadOnlyList<CiAttempt> Attempts);

public static class CiReliability
{
    private const int MaxFlakyListed = 20;

    /// <summary><paramref name="history"/> is every CI run of the pipeline, oldest first; the latest <paramref name="requested"/> are reported on.</summary>
    public static CiReliabilityReport Report(IReadOnlyList<CiReliabilityRun> history, int requested)
    {
        var window = history.TakeLast(requested).ToList();
        var ran = history.SelectMany(run => run.Attempts.Where(Ran).Select(a => new Outcome(run, a))).ToList();
        var byKey = ran.Where(o => o.Run.CodeHash is not null).ToLookup(o => (o.Run.Tree, o.Run.CodeHash, o.Attempt.StepName));
        var windowRuns = window.Select(r => r.Number).ToHashSet();

        var steps = ran.Where(o => windowRuns.Contains(o.Run.Number))
            .GroupBy(o => o.Attempt.StepName, StringComparer.Ordinal)
            .Select(step => ForStep(step.Key, [.. step], byKey, history))
            .OrderByDescending(s => s.FlakyRate)
            .ThenByDescending(s => s.FlakyFailures)
            .ThenBy(s => s.Step, StringComparer.Ordinal)
            .ToArray();

        var unknown = window.Count(r => r.CodeHash is null);
        string[] notes = unknown > 0
            ? [$"{unknown} of the runs have no code hash (their definition can't be read), so their failures count only as caught or unverified."]
            : [];
        return new CiReliabilityReport(
            window.Count > 0 ? window[0].Number : 0,
            window.Count > 0 ? window[^1].Number : 0,
            window.Count,
            requested,
            steps,
            notes);
    }

    /// <summary>Ran and reached an outcome: reused attempts and failures that aren't an outcome (never run, declined) are left out.</summary>
    private static bool Ran(CiAttempt attempt) => attempt.ReusedFrom is null && (attempt.Status is Succeeded || Category(attempt) is not (null or AttemptFailureCategory.NotAnOutcome));

    private static AttemptFailureCategory? Category(CiAttempt attempt)
        => attempt.Status is Failed failed ? AttemptFailures.Category(AttemptFailures.KindOf(failed)) : null;

    private static bool Verdict(CiAttempt attempt) => Category(attempt) is AttemptFailureCategory.Step or AttemptFailureCategory.Timeout;

    private static CiStepReliability ForStep(
        string step, List<Outcome> outcomes, ILookup<(string, string?, string), Outcome> byKey, IReadOnlyList<CiReliabilityRun> history)
    {
        var flaky = new List<CiFlakyFailure>();
        int reproduced = 0, caught = 0, unverified = 0;
        foreach (var failure in outcomes.Where(o => Verdict(o.Attempt)))
        {
            var same = failure.Run.CodeHash is null
                ? []
                : byKey[(failure.Run.Tree, failure.Run.CodeHash, step)].Where(o => o.Attempt.Id != failure.Attempt.Id).ToList();
            if (same.Where(o => o.Attempt.Status is Succeeded).MaxBy(o => o.Run.Number) is { } passed)
                flaky.Add(new CiFlakyFailure(Ref(failure), Ref(passed), [.. failure.Run.Pulls.Select(p => p.Pull)], FailedAt(failure.Attempt)));
            else if (same.Any(o => Verdict(o.Attempt)))
                reproduced++;
            else if (Category(failure.Attempt) is AttemptFailureCategory.Step && Caught(failure, step, history))
                caught++;
            else
                unverified++;
        }

        var categories = outcomes.Select(o => Category(o.Attempt)).ToList();
        return new CiStepReliability(
            step,
            outcomes.Count,
            categories.Count(c => c is not null),
            categories.Count(c => c is AttemptFailureCategory.Step),
            categories.Count(c => c is AttemptFailureCategory.Infrastructure),
            categories.Count(c => c is AttemptFailureCategory.Timeout),
            flaky.Count,
            reproduced,
            caught,
            unverified,
            (double)flaky.Count / outcomes.Count,
            [.. flaky.OrderByDescending(f => f.FailedAt).ThenByDescending(f => f.Failed.Run).Take(MaxFlakyListed)]);
    }

    /// <summary>A later pull-request run of one of the failed run's pull requests, at another head, passed the step.</summary>
    private static bool Caught(Outcome failure, string step, IReadOnlyList<CiReliabilityRun> history) =>
        history.Any(later => later.Number > failure.Run.Number && !later.Queue
            && later.Pulls.Any(p => failure.Run.Pulls.Any(f => f.Pull == p.Pull && f.Head != p.Head))
            && later.Attempts.Any(a => a.StepName == step && a.Status is Succeeded));

    private static CiAttemptRef Ref(Outcome o) => new(o.Run.Number, o.Attempt.StepName, o.Attempt.Number);

    private static DateTimeOffset FailedAt(CiAttempt attempt) => attempt.FinishedAt() ?? attempt.CreatedAt;

    private sealed record Outcome(CiReliabilityRun Run, CiAttempt Attempt);
}

/// <summary>A pipeline's CI step reliability over its latest <see cref="Runs"/> CI runs (of <see cref="RequestedRuns"/>).</summary>
public record CiReliabilityReport(int FromRun, int ToRun, int Runs, int RequestedRuns, CiStepReliability[] Steps, string[] Notes);

public record CiStepReliability(
    string Step,
    int Attempts,
    int Failures,
    int OwnFailures,
    int InfrastructureFailures,
    int Timeouts,
    int FlakyFailures,
    int ReproducedFailures,
    int CaughtFailures,
    int UnverifiedFailures,
    double FlakyRate,
    CiFlakyFailure[] Flaky);

public record CiAttemptRef(int Run, string Step, int Attempt);

public record CiFlakyFailure(CiAttemptRef Failed, CiAttemptRef Passed, int[] Pulls, DateTimeOffset FailedAt);

/// <summary>Read-only, and only ever asked for: nothing in scheduling consults it.</summary>
[OperationHandler]
public sealed class GetCiReliabilityHandler(PipelineService pipelines, CiReliabilityService reliability) : Contract.ICiRunsReliabilityHandler
{
    public Task<Contract.CiRunsReliabilityResponse> HandleAsync(Contract.CiRunsReliabilityRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Contract.CiRunsReliabilityResponse>(new Contract.CiRunsReliabilityResponse.NotFound(PipelineViews.NotFound(id)));
        return Task.FromResult<Contract.CiRunsReliabilityResponse>(reliability.Get(id, request.Runs).TryPickProblems(out var problems, out var report)
            ? new Contract.CiRunsReliabilityResponse.BadRequest(ApiProblems.Of([.. problems]))
            : new Contract.CiRunsReliabilityResponse.Ok(ToApi(report)));
    }

    public static Contract.CiReliabilityReport ToApi(CiReliabilityReport r) => new()
    {
        FromRun = r.FromRun,
        ToRun = r.ToRun,
        Runs = r.Runs,
        RequestedRuns = r.RequestedRuns,
        Notes = r.Notes,
        Steps = [.. r.Steps.Select(s => new Contract.CiStepReliability
        {
            Step = s.Step,
            Attempts = s.Attempts,
            Failures = s.Failures,
            OwnFailures = s.OwnFailures,
            InfrastructureFailures = s.InfrastructureFailures,
            Timeouts = s.Timeouts,
            FlakyFailures = s.FlakyFailures,
            ReproducedFailures = s.ReproducedFailures,
            CaughtFailures = s.CaughtFailures,
            UnverifiedFailures = s.UnverifiedFailures,
            FlakyRate = s.FlakyRate,
            Flaky = [.. s.Flaky.Select(f => new Contract.CiFlakyFailure
            {
                Failed = Ref(f.Failed),
                Passed = Ref(f.Passed),
                Pulls = f.Pulls,
                FailedAt = f.FailedAt,
            })],
        })],
    };

    private static Contract.CiAttemptRef Ref(CiAttemptRef r) => new() { Run = r.Run, Step = r.Step, Attempt = r.Attempt };
}
