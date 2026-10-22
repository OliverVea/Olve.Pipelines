using System.Text.Json;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Explicit reruns and continues within a run (spec §5.2). All require the run to be pinned to the
/// pipeline's Active version: after a switch, old runs are un-continuable (re-trigger instead).
/// <list type="bullet">
/// <item><see cref="RerunStep"/> adds an attempt of one step with the same pinned version and the
/// current pool input. Allowed when the run has finished, or the step's latest attempt failed or was
/// cancelled (the run is then still at that stage's barrier). <b>Rerun only</b> on a finished run leaves
/// the run's status alone; <b>rerun and continue</b> re-opens it, and the scheduler re-evaluates every
/// later stage against the latest successful attempts (steps whose input changed run again). In a run
/// that is still Running, a rerun always behaves as continue (the barrier picks it up).</item>
/// <item><see cref="ContinueRun"/> re-opens a run with a changed input, keeping completed stages and
/// re-evaluating <c>when</c> for the rest. A field may change only if no completed step consumed it:
/// consumed = the <c>input.*</c> refs of every completed step's mappings (<c>{ref:"input"}</c> = all
/// fields) plus the <c>when</c> fields enclosing it.</item>
/// </list>
/// Continuing also retries the not-yet-completed stages' failed/cancelled steps (via <see cref="Run.ResumedAt"/>).
/// </summary>
public class RunContinuationService(
    RunService runs,
    AttemptService attempts,
    RunPlanCache plans,
    StepGateService gates,
    PipelineActivationService activations,
    StageScheduler scheduler,
    RunSchedulingLock schedulingLock)
{
    public Result<Attempt> RerunStep(Id<Run> runId, string stepName, bool continueAfter) => schedulingLock.Run<Result<Attempt>>(() =>
    {
        if (!runs.TryGet(runId, out var run))
            return new NotFoundProblem("Run '{0}' not found.", runId);
        if (RequireActiveVersion(run) is { } versionProblem)
            return versionProblem;
        if (plans.Get(run.VersionId).TryPickProblems(out var planProblems, out var plan))
            return planProblems;
        if (!plan.TryGetStep(stepName, out var stage, out var step))
            return new NotFoundProblem("Run #{0} has no step '{1}'.", run.Number, stepName);
        if (gates.IsBlocked(run.PipelineId, stepName))
            return new ResultProblem("Step '{0}' is blocked by its promotion gate.", stepName);

        var stepAttempts = attempts.ListForStep(run.Id, stepName);
        if (stepAttempts.Any(a => a.Status.IsActive()))
            return new ResultProblem("Step '{0}' already has an active attempt in run #{1}.", stepName, run.Number);

        var latest = stepAttempts.LastOrDefault();
        if (run.Status is RunStatus.Running && latest?.Status is not (Failed or Cancelled))
            return new ResultProblem("Step '{0}' can be rerun only once run #{1} has finished or the step's latest attempt failed.", stepName, run.Number);
        if (continueAfter && run.Status is RunStatus.Superseded)
            return new ResultProblem("Run #{0} was superseded by a newer run and cannot be continued.", run.Number);

        if (!StageScheduler.TryParseInput(run.Input, out var input))
            return new ResultProblem("Run #{0} has malformed input.", run.Number);

        switch (StageScheduler.Decide(stage, step, new RunPool(run.Id, input, attempts)))
        {
            case StepDecision.Skip skip:
                return new ResultProblem("Step '{0}' cannot run in run #{1}: {2}.", stepName, run.Number, skip.Reason);
            case StepDecision.Fail fail:
                return new ResultProblem("Step '{0}' cannot run in run #{1}: {2}.", stepName, run.Number, fail.Reason);
            case StepDecision.Schedule schedule:
                if (continueAfter && run.Status.IsTerminal() && runs.Reopen(run.Id).TryPickProblems(out var reopenProblems))
                    return reopenProblems;

                if (attempts.Schedule(run.Id, stepName, schedule.Hash, schedule.Json).TryPickProblems(out var problems, out var attempt))
                    return problems;

                scheduler.Advance(run.Id);
                return attempt;
            default:
                return new ResultProblem("Unknown decision for step '{0}'.", stepName);
        }
    });

    public Result<Run> ContinueRun(Id<Run> runId, string newInputJson) => schedulingLock.Run<Result<Run>>(() =>
    {
        if (!runs.TryGet(runId, out var run))
            return new NotFoundProblem("Run '{0}' not found.", runId);
        if (run.Status is RunStatus.Superseded)
            return new ResultProblem("Run #{0} was superseded by a newer run and cannot be continued.", run.Number);
        if (RequireActiveVersion(run) is { } versionProblem)
            return versionProblem;
        if (attempts.HasActiveAttempts(run.Id))
            return new ResultProblem("Run #{0} still has active attempts; continue it once they finish.", run.Number);
        if (plans.Get(run.VersionId).TryPickProblems(out var planProblems, out var plan))
            return planProblems;
        if (!StageScheduler.TryParseInput(run.Input, out var oldInput))
            return new ResultProblem("Run #{0} has malformed input.", run.Number);
        if (!StageScheduler.TryParseInput(newInputJson, out var newInput))
            return new ResultProblem("Run input must be a JSON object.");

        var consumers = ConsumedFields(run, plan);
        var violations = new List<ResultProblem>();
        foreach (var field in ChangedFields(oldInput, newInput))
        {
            var by = consumers.TryGetValue(field, out var direct) ? direct : [];
            if (consumers.TryGetValue(AttemptInputBuilder.WholeInput, out var whole))
                by = [.. by, .. whole];
            if (by.Count > 0)
                violations.Add(new ResultProblem("Field '{0}' cannot change: completed step(s) {1} consumed it.",
                    field, string.Join(", ", by.Distinct().Order(StringComparer.Ordinal))));
        }

        if (violations.Count > 0)
            return Result.Failure<Run>(violations);

        if (runs.Reopen(run.Id, newInputJson).TryPickProblems(out var problems, out var reopened))
            return problems;

        scheduler.Advance(run.Id);
        return runs.TryGet(run.Id, out var current) ? current : reopened;
    });

    /// <summary>
    /// Pipeline-input field → completed steps that consumed it. Completed = latest attempt succeeded or
    /// skipped by its own handler (i.e. it ran). Includes the <c>when</c> fields enclosing such a step.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> ConsumedFields(Run run, RunPlan plan)
    {
        var consumers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var stage in plan.Stages)
        {
            foreach (var step in stage.Steps)
            {
                var latest = attempts.ListForStep(run.Id, step.Name).LastOrDefault();
                if (latest is null || latest.Status is not (Succeeded or Skipped) || latest.IsSchedulerSkip())
                    continue;

                foreach (var field in AttemptInputBuilder.ConsumedInputFields(step).Concat(stage.ConditionFields))
                {
                    if (!consumers.TryGetValue(field, out var list))
                        consumers[field] = list = [];
                    list.Add(step.Name);
                }
            }
        }

        return consumers;
    }

    private static IEnumerable<string> ChangedFields(JsonElement before, JsonElement after)
    {
        var names = before.EnumerateObject().Select(p => p.Name)
            .Concat(after.EnumerateObject().Select(p => p.Name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var hadBefore = before.TryGetProperty(name, out var b);
            var hasAfter = after.TryGetProperty(name, out var a);
            if (hadBefore != hasAfter || (hadBefore && !JsonElement.DeepEquals(a, b)))
                yield return name;
        }
    }

    private ResultProblem? RequireActiveVersion(Run run)
    {
        if (activations.TryGet(run.PipelineId, out var activation)
            && activation is PipelineActivation.Active active
            && active.VersionId == run.VersionId)
            return null;

        return new ResultProblem(
            "Run #{0} is pinned to version {1}, which is not the pipeline's active version; re-trigger the pipeline instead.",
            run.Number, run.VersionNumber);
    }
}
