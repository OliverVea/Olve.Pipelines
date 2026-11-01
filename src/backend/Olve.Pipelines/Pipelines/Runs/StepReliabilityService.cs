using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// How reliable a pipeline's steps were over its latest runs, from attempt outcomes. A failure rate alone says
/// nothing is wrong: a test step exists to fail. What is singled out is a step failing more than it usually does,
/// a failure the same code and input did not repeat (flaky), and a failure of the platform around the step. Those
/// last two are failures for no real reason. Read-only; nothing in scheduling consults it. Stateless query; transient.
/// </summary>
public class StepReliabilityService(RunService runs, AttemptService attempts, RunPlanCache plans, PipelineVersionService versions)
{
    public const int DefaultRuns = 100;
    public const int MaxRuns = 500;

    private const int RecentAttempts = 10;
    private const int MinUsualAttempts = 10;
    private const int MinRecentFailures = 3;
    private const double AboveUsualBy = 0.25;

    public Result<ReliabilityReport> Get(Id<Pipeline> pipelineId, int? requestedRuns = null)
    {
        var requested = requestedRuns ?? DefaultRuns;
        if (requested is < 1 or > MaxRuns)
            return ValidationProblem.ForField("runs", "runs must be between 1 and {0}, not {1}.", MaxRuns, requested);

        var all = runs.ListByPipeline(pipelineId);
        var window = all.Take(requested).Reverse().ToList();
        var notes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var run in window)
        {
            if (plans.ForRun(run).TryPickProblems(out _, out _))
                notes.Add($"The plan of version {run.VersionNumber} can't be read: its automatic retries aren't known, and its attempts compare only within that version.");
        }

        var outcomes = window
            .SelectMany(run => attempts.ListByRun(run.Id).Select(a => (Attempt: a, Run: run)))
            .Where(x => x.Attempt.Status is AttemptStatus.Succeeded || Failure(x.Attempt) is not (null or AttemptFailureCategory.NotAnOutcome))
            .Select(x => new Outcome(x.Attempt, x.Run, Failure(x.Attempt)))
            .ToList();

        var steps = new List<StepReliability>();
        var noReason = new HashSet<Id<Attempt>>();
        foreach (var step in outcomes.GroupBy(o => o.Attempt.StepName, StringComparer.Ordinal))
            steps.Add(ForStep(step.Key, step.OrderBy(o => o.Run.Number).ThenBy(o => o.Attempt.Number).ToList(), noReason));

        return new ReliabilityReport(
            ForWindow(all, window, requested),
            ForRuns(window, outcomes, noReason),
            steps
                .OrderByDescending(s => s.AboveUsual)
                .ThenByDescending(s => s.FlakyFailures)
                .ThenByDescending(s => s.FailureRate)
                .ThenBy(s => s.Step, StringComparer.Ordinal)
                .ToArray(),
            notes.ToArray());
    }

    private static AttemptFailureCategory? Failure(Attempt attempt)
        => attempt.Status is AttemptStatus.Failed failed ? AttemptFailures.Category(AttemptFailures.KindOf(failed)) : null;

    private static ReliabilityWindow ForWindow(IReadOnlyList<Run> all, List<Run> window, int requested) => new(
        window.Count > 0 ? window[0].Number : 0,
        window.Count > 0 ? window[^1].Number : 0,
        window.Count,
        requested,
        window.Count > 0 ? window[0].CreatedAt : null,
        window.Count > 0 ? window[^1].CreatedAt : null,
        window.Count < requested && window.Count > 0 && window[0].Number > 1);

    private StepReliability ForStep(string step, List<Outcome> outcomes, HashSet<Id<Attempt>> noReason)
    {
        var failures = outcomes.Where(o => o.Failure is not null).ToList();
        foreach (var failure in failures.Where(f => f.Failure is not AttemptFailureCategory.Step))
            noReason.Add(failure.Attempt.Id);

        var flaky = new List<Outcome>();
        var reproduced = 0;
        if (failures.Any(f => f.Failure is AttemptFailureCategory.Step))
        {
            foreach (var same in outcomes.GroupBy(o => Identity(o.Run, o.Attempt)))
            {
                var own = same.Where(o => o.Failure is AttemptFailureCategory.Step).ToList();
                if (same.Any(o => o.Failure is null))
                    flaky.AddRange(own);
                else if (own.Count > 1)
                    reproduced += own.Count;
            }
        }
        foreach (var failure in flaky)
            noReason.Add(failure.Attempt.Id);

        var ownFailures = failures.Count(f => f.Failure is AttemptFailureCategory.Step);
        var (recentRate, usualRate, aboveUsual) = Trend(outcomes);
        return new StepReliability(
            step,
            outcomes.Count,
            failures.Count,
            ownFailures,
            failures.Count(f => f.Failure is AttemptFailureCategory.Infrastructure),
            failures.Count(f => f.Failure is AttemptFailureCategory.Timeout),
            (double)failures.Count / outcomes.Count,
            recentRate,
            usualRate,
            aboveUsual,
            flaky.Count,
            reproduced,
            ownFailures - flaky.Count - reproduced,
            (double)flaky.Count / outcomes.Count,
            flaky.Select(f => f.Run.Number).Distinct().OrderDescending().ToArray());
    }

    /// <summary>Compares a step with itself, so a step that always fails a lot is usual and one that starts to is not.</summary>
    private static (double? Recent, double? Usual, bool AboveUsual) Trend(List<Outcome> outcomes)
    {
        if (outcomes.Count < RecentAttempts + MinUsualAttempts)
            return (null, null, false);
        var usual = outcomes.SkipLast(RecentAttempts).ToList();
        var recentFailures = outcomes.TakeLast(RecentAttempts).Count(o => o.Failure is not null);
        var recentRate = (double)recentFailures / RecentAttempts;
        var usualRate = (double)usual.Count(o => o.Failure is not null) / usual.Count;
        return (recentRate, usualRate, recentFailures >= MinRecentFailures && recentRate - usualRate >= AboveUsualBy);
    }

    private ReliabilityRuns ForRuns(List<Run> window, List<Outcome> outcomes, HashSet<Id<Attempt>> noReason)
    {
        var settled = window.Where(r => r.Status is RunStatus.Succeeded or RunStatus.Failed).ToList();
        var failuresByRun = outcomes.Where(o => o.Failure is not null).ToLookup(o => o.Run.Id);
        var stopped = new List<int>();
        var absorbed = 0;
        foreach (var run in settled)
        {
            var hit = false;
            var retried = false;
            foreach (var step in failuresByRun[run.Id].GroupBy(o => o.Attempt.StepName, StringComparer.Ordinal))
            {
                var retries = Retries(run, step.Key);
                var ordered = step.OrderBy(o => o.Attempt.Number).ToList();
                hit |= ordered.Skip(retries).Any(o => noReason.Contains(o.Attempt.Id));
                retried |= ordered.Take(retries).Any(o => noReason.Contains(o.Attempt.Id));
            }

            if (hit)
                stopped.Add(run.Number);
            else if (retried)
                absorbed++;
        }

        return new ReliabilityRuns(
            settled.Count,
            stopped.Count,
            settled.Count > 0 ? (double)stopped.Count / settled.Count : 0,
            absorbed,
            stopped.OrderDescending().ToArray());
    }

    private int Retries(Run run, string step)
        => !plans.ForRun(run).TryPickProblems(out _, out var plan) && plan.TryGetStep(step, out _, out var descriptor)
            ? (int)(descriptor.Retries ?? 0)
            : 0;

    /// <summary>What makes two attempts the same execution: the step's code and definition, the run's config, and the input.</summary>
    private string Identity(Run run, Attempt attempt)
    {
        var code = !plans.ForRun(run).TryPickProblems(out _, out var plan) && plan.TryGetStep(attempt.StepName, out _, out var step)
            ? plan.Manifest.CodeHash ?? step.HandlerHash
            : null;
        var definition = versions.TryGet(run.VersionId, out var version) ? version.StepVersions.GetValueOrDefault(attempt.StepName) : 0;
        return $"{code ?? $"v{run.VersionNumber}"}|{definition}|{run.ConfigSnapshot}|{InputIdentity(attempt)}";
    }

    /// <summary>
    /// The attempt's input, not <see cref="Attempt.InputHash"/> alone: that hash is taken before external answers are
    /// added, and a file ref names the run and attempt that made the file, so the same file differs between runs.
    /// </summary>
    private static string InputIdentity(Attempt attempt)
    {
        if (attempt.Input is null)
            return attempt.InputHash ?? "";
        if (attempt.Answers is null && attempt.InputHash is { } hash && !attempt.Input.Contains("\"sha256\"", StringComparison.Ordinal))
            return hash;

        var input = JsonNode.Parse(attempt.Input);
        DropFileOrigins(input);
        using var document = JsonDocument.Parse(input?.ToJsonString() ?? "null");
        return CanonicalJson.Hash(document.RootElement);
    }

    private static void DropFileOrigins(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject file when IsFileRef(file):
                file.Remove("run");
                file.Remove("step");
                file.Remove("attempt");
                break;
            case JsonObject value:
                foreach (var (_, child) in value)
                    DropFileOrigins(child);
                break;
            case JsonArray items:
                foreach (var item in items)
                    DropFileOrigins(item);
                break;
        }
    }

    private static bool IsFileRef(JsonObject value)
        => value.Count == 5 && value.ContainsKey("run") && value.ContainsKey("step") && value.ContainsKey("attempt") && value.ContainsKey("path") && value.ContainsKey("sha256");

    private sealed record Outcome(Attempt Attempt, Run Run, AttemptFailureCategory? Failure);
}

/// <summary>A pipeline's step reliability over <see cref="ReliabilityReport.Window"/>; <see cref="Notes"/> says what it couldn't work out.</summary>
public record ReliabilityReport(ReliabilityWindow Window, ReliabilityRuns Runs, StepReliability[] Steps, string[] Notes);

/// <summary>The runs the numbers cover: the latest <see cref="RequestedRuns"/>, or fewer when there are no more.</summary>
/// <param name="From">When the first covered run started (null: no runs).</param>
/// <param name="To">When the last covered run started (null: no runs).</param>
/// <param name="LimitedByRetention">Fewer runs than requested, because older ones were deleted.</param>
public record ReliabilityWindow(int FromRun, int ToRun, int Runs, int RequestedRuns, DateTimeOffset? From, DateTimeOffset? To, bool LimitedByRetention);

/// <summary>
/// The chance a run fails for no real reason, as observed: of the <see cref="Settled"/> runs (succeeded or failed;
/// a rerun can have rescued them), those a failure stopped that was flaky or not the step's own.
/// </summary>
/// <param name="Absorbed">Other settled runs where such a failure happened and a step's automatic retries covered it.</param>
public record ReliabilityRuns(int Settled, int FailedForNoReason, double Rate, int Absorbed, int[] RunNumbers);

/// <summary>One step's attempts that ran to an outcome (succeeded or failed; a declined or never-run attempt is neither).</summary>
/// <param name="Failures">All failed attempts: <see cref="OwnFailures"/> + <see cref="InfrastructureFailures"/> + <see cref="Timeouts"/>.</param>
/// <param name="RecentFailureRate">The rate over the latest attempts (null: too few attempts to compare).</param>
/// <param name="UsualFailureRate">The rate over the attempts before those (null: too few attempts to compare).</param>
/// <param name="AboveUsual">The step fails clearly more than it usually does.</param>
/// <param name="FlakyFailures">Own failures where the same code and input also succeeded.</param>
/// <param name="ReproducedFailures">Own failures where the same code and input failed again and never succeeded.</param>
/// <param name="UnverifiedFailures">Own failures whose code and input never ran again.</param>
/// <param name="FlakyRate"><see cref="FlakyFailures"/> over <see cref="Attempts"/>.</param>
/// <param name="FlakyRuns">The runs with a flaky failure, newest first.</param>
public record StepReliability(
    string Step,
    int Attempts,
    int Failures,
    int OwnFailures,
    int InfrastructureFailures,
    int Timeouts,
    double FailureRate,
    double? RecentFailureRate,
    double? UsualFailureRate,
    bool AboveUsual,
    int FlakyFailures,
    int ReproducedFailures,
    int UnverifiedFailures,
    double FlakyRate,
    int[] FlakyRuns);
