using Olve.Pipelines.Pipelines.Scheduling;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// The critical path of a range of runs. Stages are barriers, so a stage's critical step is the one whose
/// attempts finished last; it is charged from the stage's first attempt to that finish. Only one pass
/// counts per stage: attempts created while the stage was running (automatic retries), none created
/// after the next stage started (a later rerun). Time between stages (a held run, a continue) is idle,
/// charged to no step. Stateless query; transient.
/// </summary>
public class CriticalPathService(RunService runs, AttemptService attempts, RunPlanCache plans)
{
    public const int MaxRuns = 500;

    /// <summary>A retry is created as its predecessor fails; allow for the scheduler's latency.</summary>
    private static readonly TimeSpan RetrySlack = TimeSpan.FromSeconds(30);

    /// <param name="sinceVersion">Without <paramref name="from"/>: start at the first run on this version or a later one (the latest <see cref="MaxRuns"/> at most).</param>
    public Result<CriticalPathReport> Get(Id<Pipeline> pipelineId, int? from, int? to, bool includeUnsuccessful, int? sinceVersion = null)
    {
        var all = runs.ListByPipeline(pipelineId);
        if (all.Count == 0)
            return new CriticalPathReport(0, 0, [], [], [], 0, 0);

        var hi = to ?? all.Max(r => r.Number);
        int lo;
        if (from is { } f)
            lo = f;
        else if (sinceVersion is { } v)
        {
            var first = all.Where(r => r.VersionNumber >= v && r.Number <= hi).Select(r => r.Number).DefaultIfEmpty().Min();
            if (first == 0)
                return new CriticalPathReport(0, 0, [], [], [], 0, 0);
            lo = Math.Max(first, hi - MaxRuns + 1);
        }
        else
            lo = Math.Max(1, hi - 19);
        if (lo > hi)
            return ValidationProblem.ForField("from", "from ({0}) is after to ({1}).", lo, hi);
        if (hi - lo + 1 > MaxRuns)
            return ValidationProblem.ForField("from", "At most {0} runs at a time; narrow the range.", MaxRuns);

        var reported = new List<CriticalPathRun>();
        var skipped = new List<CriticalPathSkip>();
        foreach (var run in all.Where(r => r.Number >= lo && r.Number <= hi).OrderBy(r => r.Number))
        {
            if (run.Status is RunStatus.Running)
                skipped.Add(new CriticalPathSkip(run.Number, "still running"));
            else if (run.Status is not RunStatus.Succeeded && !includeUnsuccessful)
                skipped.Add(new CriticalPathSkip(run.Number, run.Status.ToString()));
            else if (plans.ForRun(run).TryPickProblems(out _, out var plan) || !StageScheduler.TryParseInput(run.Input, out var input))
                skipped.Add(new CriticalPathSkip(run.Number, "its plan can't be read"));
            else
                reported.Add(ForRun(run, plan, input));
        }

        var total = reported.Sum(r => r.Seconds);
        var steps = reported
            .SelectMany(r => r.Path.Select(p => (Run: r.Number, Stage: p)))
            .GroupBy(x => x.Stage.Step, StringComparer.Ordinal)
            .Select(g => new CriticalPathStep(
                g.Key,
                g.Count(),
                g.Sum(x => x.Stage.Seconds),
                total > 0 ? g.Sum(x => x.Stage.Seconds) / total : 0,
                g.Average(x => x.Stage.Seconds),
                g.Sum(x => x.Stage.WaitSeconds),
                g.Select(x => x.Run).ToArray(),
                AverageOverhead(g.Select(x => x.Stage))))
            .OrderByDescending(s => s.Seconds)
            .ThenBy(s => s.Step, StringComparer.Ordinal)
            .ToArray();

        return new CriticalPathReport(lo, hi, reported.ToArray(), steps, skipped.ToArray(), total, reported.Count > 0 ? total / reported.Count : 0);
    }

    private CriticalPathRun ForRun(Run run, RunPlan plan, System.Text.Json.JsonElement input)
    {
        var byStep = attempts.ListByRun(run.Id).ToLookup(a => a.StepName, StringComparer.Ordinal);
        var stages = plan.Stages
            .Where(stage => stage.EvaluateWhen(input) is null)
            .Select(stage => (stage.Index, Attempts: stage.Steps.SelectMany(s => byStep[s.Name]).Where(a => a.FinishedAt() is not null).OrderBy(a => a.CreatedAt).ToList()))
            .Where(s => s.Attempts.Count > 0)
            .ToList();

        var path = new List<CriticalPathStage>();
        var notes = new List<string>();
        var idle = 0.0;
        DateTimeOffset? previousEnd = null;
        for (var i = 0; i < stages.Count; i++)
        {
            var nextStart = i + 1 < stages.Count ? stages[i + 1].Attempts[0].CreatedAt : (DateTimeOffset?)null;
            var pass = new List<Attempt>();
            var passEnd = DateTimeOffset.MinValue;
            foreach (var attempt in stages[i].Attempts)
            {
                var belongs = pass.Count == 0
                              || (attempt.CreatedAt <= passEnd + RetrySlack && (nextStart is null || attempt.CreatedAt < nextStart));
                if (!belongs)
                {
                    notes.Add($"{attempt.StepName}@{attempt.Number} left out: it ran after the stage had finished (a rerun)");
                    continue;
                }
                pass.Add(attempt);
                if (attempt.FinishedAt() > passEnd)
                    passEnd = attempt.FinishedAt()!.Value;
            }

            var start = pass[0].CreatedAt;
            if (previousEnd is { } end && start > end)
                idle += (start - end).TotalSeconds;
            var last = pass.MaxBy(a => a.FinishedAt())!;
            var mine = pass.Where(a => a.StepName == last.StepName).ToList();
            var firstStart = mine.Select(a => a.StartedAt()).FirstOrDefault(t => t is not null) ?? last.FinishedAt()!.Value;
            path.Add(new CriticalPathStage(
                stages[i].Index,
                last.StepName,
                mine.Count,
                Math.Max(0, (passEnd - start).TotalSeconds),
                Math.Max(0, (firstStart - start).TotalSeconds),
                SumKnown(mine.Select(a => AttemptPhases.From(a)?.Active))));
            previousEnd = passEnd;
        }

        return new CriticalPathRun(run.Number, run.Status, run.VersionNumber, run.VersionStamp, path.Sum(p => p.Seconds), idle, path.ToArray(), notes.ToArray(),
            SumKnown(path.Select(p => p.ActiveSeconds)));
    }

    /// <summary>The average time outside the step container, over the stages whose attempts have timings (null: none have).</summary>
    private static double? AverageOverhead(IEnumerable<CriticalPathStage> stages)
    {
        var known = stages.Where(s => s.ActiveSeconds is not null).Select(s => s.Seconds - s.ActiveSeconds!.Value).ToList();
        return known.Count > 0 ? known.Average() : null;
    }

    /// <summary>The sum, or null when any part is unknown (attempts from before timings were recorded).</summary>
    private static double? SumKnown(IEnumerable<double?> values)
    {
        var sum = 0.0;
        foreach (var v in values)
        {
            if (v is null)
                return null;
            sum += v.Value;
        }
        return sum;
    }
}

/// <summary>The critical path of runs <see cref="From"/>..<see cref="To"/>; <see cref="Steps"/> sums each step's share of it.</summary>
public record CriticalPathReport(
    int From,
    int To,
    CriticalPathRun[] Runs,
    CriticalPathStep[] Steps,
    CriticalPathSkip[] Skipped,
    double TotalSeconds,
    double AverageSeconds);

/// <summary>One run's critical path: one entry per stage. <see cref="IdleSeconds"/> is time between stages charged to no step; <see cref="ActiveSeconds"/> the path's time in step containers.</summary>
public record CriticalPathRun(
    int Number,
    RunStatus Status,
    int VersionNumber,
    string VersionStamp,
    double Seconds,
    double IdleSeconds,
    CriticalPathStage[] Path,
    string[] Notes,
    double? ActiveSeconds = null);

/// <summary>
/// A stage's critical step: <see cref="Seconds"/> from the stage's first attempt to its last finish, <see cref="WaitSeconds"/>
/// of it before the step's first attempt started, <see cref="ActiveSeconds"/> of it in the step container (null when unknown).
/// </summary>
public record CriticalPathStage(int Stage, string Step, int Attempts, double Seconds, double WaitSeconds, double? ActiveSeconds = null);

/// <summary>A step's part in the critical path of the reported runs: in <see cref="RunNumbers"/>, for <see cref="Share"/> (0..1) of the total.</summary>
/// <param name="OverheadSeconds">Average time outside the step container when on the path, over the runs with timings (null: none have).</param>
public record CriticalPathStep(string Step, int Runs, double Seconds, double Share, double AverageSeconds, double WaitSeconds, int[] RunNumbers, double? OverheadSeconds = null);

/// <summary>A run in the range that isn't reported, and why.</summary>
public record CriticalPathSkip(int Number, string Reason);
