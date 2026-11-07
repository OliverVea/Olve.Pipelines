using System.Text;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Drives a run through its stages. <see cref="Advance"/> is idempotent and
/// stateless: it re-derives everything from the store, walking the flattened stages from the start.
/// For each step of the first unfinished stage it decides:
/// <list type="number">
/// <item><b>when</b> — every enclosing condition must match the run input, else the step is
/// <b>excluded</b>: not part of this run, with no attempt (<c>when deployTo ∉ [prod]</c>);</item>
/// <item><b>inputs</b> — a required input referencing a skipped/unproduced output skips the step with a
/// chained reason; optional ones are left out;</item>
/// <item><b>blocked</b> — a blocked step gets no attempt and holds the run (<see cref="Run.HeldAt"/>);</item>
/// <item>otherwise it schedules an attempt carrying the assembled input and its hash.</item>
/// </list>
/// A step whose latest attempt already matches the decision is settled: a succeeded (or handler-skipped)
/// attempt with the same input hash, a scheduler skip (no input) with the same reason, or a failed/cancelled/obsolete attempt that
/// is not stale (older than <see cref="Run.ResumedAt"/>). Hash freshness is what makes continue and
/// rerun-and-continue re-run exactly the downstream steps whose inputs changed.
/// <para><b>Barrier.</b> The next stage starts only when every step of the current one is settled and
/// none is active or held. Then: any Failed → run Failed; any Cancelled → run Cancelled; an attempt
/// obsoleted by a newer run's attempt (latest-wins) → run Superseded by that run; otherwise continue.
/// All stages done → Succeeded.</para>
/// <para><b>Versions.</b> New attempts are only created while the run's version is the pipeline's
/// Active version; during a drain the run's in-flight attempts finish but it starts nothing new.</para>
/// Transient; serialised by <see cref="RunSchedulingLock"/>.
/// </summary>
public class StageScheduler(
    RunService runs,
    AttemptService attempts,
    RunPlanCache plans,
    StepBlockService blocks,
    PipelineActivationService activations,
    RunCancellationService cancellation,
    RunSchedulingLock schedulingLock,
    ILogger<StageScheduler> logger)
{
    public void Advance(Id<Run> runId) => schedulingLock.Advance(runId, () => AdvanceOnce(runId));

    /// <summary>Advances every Running run of a pipeline, oldest first (blocked-step changes, startup).</summary>
    public void AdvanceRunning(Id<Pipeline> pipelineId) => schedulingLock.Run(() =>
    {
        foreach (var run in runs.ListByPipeline(pipelineId).Where(r => r.Status is RunStatus.Running).OrderBy(r => r.Number))
            Advance(run.Id);
    });

    /// <summary>What the scheduler would do with a step right now (also used by reruns).</summary>
    internal static StepDecision Decide(PlanStage stage, StepDescriptor step, IRunPool pool)
    {
        if (stage.EvaluateWhen(pool.Input) is { } whenReason)
            return new StepDecision.Excluded(whenReason);

        return AttemptInputBuilder.Build(step, pool) switch
        {
            AttemptInputResult.Unavailable u => new StepDecision.Skip(u.Reason),
            AttemptInputResult.Built b when Encoding.UTF8.GetByteCount(b.Json) > AttemptService.MaxInputJsonBytes
                => new StepDecision.Fail($"input.json exceeds {AttemptService.MaxInputJsonBytes} bytes", b.Hash),
            AttemptInputResult.Built b => new StepDecision.Schedule(b.Json, b.Hash),
            _ => throw new InvalidOperationException("Unknown input result."),
        };
    }

    private void AdvanceOnce(Id<Run> runId)
    {
        if (!runs.TryGet(runId, out var run) || run.Status.IsTerminal())
            return;

        if (plans.ForRun(run).TryPickProblems(out var planProblems, out var plan))
        {
            logger.LogProblems(LogLevel.Error, planProblems,
                "Run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' cannot be scheduled: its version {VersionNumber} has no usable manifest; failing the run",
                run.Number, run.Id, run.PipelineId, run.VersionNumber);
            LogIfFailed(runs.Complete(run.Id, RunStatus.Failed), run, "fail");
            return;
        }

        if (!TryParseInput(run.Input, out var input))
        {
            logger.LogError("Run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' has malformed input; failing the run",
                run.Number, run.Id, run.PipelineId);
            LogIfFailed(runs.Complete(run.Id, RunStatus.Failed), run, "fail");
            return;
        }

        // Runs on any version carry on once a switch is complete; only a cancelling drain stops them.
        var canStartWork = activations.TryGet(run.PipelineId, out var activation) && activation is PipelineActivation.Active;
        var pool = new RunPool(run.Id, input, plan, attempts);

        foreach (var stage in plan.Stages)
        {
            var waiting = false;
            string? heldAt = null;
            // Cells of a matrix step share one decision: the same input.
            var decisions = new Dictionary<StepDescriptor, StepDecision>(ReferenceEqualityComparer.Instance);
            StepDecision DecisionFor(StepDescriptor step) =>
                decisions.TryGetValue(step, out var d) ? d : decisions[step] = Decide(stage, step, pool);

            foreach (var step in stage.Steps.Where(s => s.Matrix is { Cascades: true }))
                Cascade(run, stage, step, DecisionFor(step));

            foreach (var unit in stage.Units)
            {
                var step = unit.Step;
                var latest = Latest(run.Id, unit.Name);
                if (latest is not null && latest.Status.IsActive())
                {
                    waiting = true;
                    continue;
                }

                var decision = DecisionFor(step);
                if (decision is StepDecision.Excluded)
                    continue;
                if (IsSettled(latest, decision, run) && !RetryDue(run, unit, decision, log: true))
                    continue;

                // A blocked matrix step holds every cell.
                if (decision is StepDecision.Schedule && blocks.IsBlocked(run.PipelineId, step.Name))
                {
                    heldAt ??= step.Name;
                    waiting = true;
                    continue;
                }

                if (!canStartWork)
                {
                    waiting = true;
                    continue;
                }

                if (decision is StepDecision.Schedule && OvertakenBy(run, unit.Name) is { } newer)
                {
                    SupersedeOvertaken(run, newer, unit.Name);
                    return;
                }

                Act(run, unit, decision);
                if (Latest(run.Id, unit.Name) is { } created && created.Status.IsActive())
                    waiting = true;
            }

            SetHeldAt(run, heldAt);
            if (waiting)
                return;

            if (ApplyStageOutcome(run, stage))
                return;
        }

        if (runs.Complete(run.Id, RunStatus.Succeeded).TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not complete run #{RunNumber} ('{RunId}')", run.Number, run.Id);
        else
            logger.LogInformation("Run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' succeeded", run.Number, run.Id, run.PipelineId);
    }

    /// <summary>
    /// The newest run that already started <paramref name="stepName"/> although it is newer than
    /// <paramref name="run"/>: running the step now would put older work after newer work (e.g. deploy
    /// an older build over a newer one). Attempts from before an explicit resume of <paramref name="run"/>
    /// don't count: the operator asked for this run to go on.
    /// </summary>
    private Run? OvertakenBy(Run run, string stepName) =>
        runs.ListByPipeline(run.PipelineId)
            .Where(r => r.Number > run.Number)
            .Where(r => attempts.ListForStep(r.Id, stepName)
                .Any(a => a.Status is not Obsolete && !(run.ResumedAt is { } resumed && a.CreatedAt < resumed)))
            .MaxBy(r => r.Number);

    private void SupersedeOvertaken(Run run, Run newer, string stepName)
    {
        logger.LogInformation("Run #{RunNumber} ('{RunId}') superseded by run #{NewerRunNumber}: the newer run already started step '{StepName}'",
            run.Number, run.Id, newer.Number, stepName);
        LogIfFailed(runs.Supersede(run.Id, newer.Id), run, "supersede");
        foreach (var waiting in attempts.ListByRun(run.Id).Where(a => a.Status is Scheduled or Waiting))
        {
            if (attempts.Cancel(waiting.Id).TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Warning, problems, "Could not cancel step '{StepName}' of superseded run #{RunNumber}", waiting.StepName, run.Number);
        }
    }

    private static bool IsSettled(Attempt? latest, StepDecision decision, Run run)
    {
        if (latest is null)
            return false;

        return latest.Status switch
        {
            Failed or Cancelled or Obsolete => !(run.ResumedAt is { } resumed && latest.CreatedAt < resumed),
            Skipped skipped when latest.IsSchedulerSkip() => decision is StepDecision.Skip s && s.Reason == skipped.Reason,
            Succeeded or Skipped => decision is StepDecision.Schedule sc && sc.Hash == latest.InputHash,
            _ => false,
        };
    }

    /// <summary>
    /// Automatic retry (a step opts in with <c>retries: n</c>, an idempotency claim): the
    /// step's latest attempt failed, the decision is to run it, and the trailing streak of failed
    /// attempts since the run was last resumed is at most <see cref="StepDescriptor.Retries"/> — so a
    /// step gets 1 + n consecutive tries. Cancelled/obsolete attempts and scheduler-side failures
    /// (<see cref="StepDecision.Fail"/>) are never retried; a continue resets the count.
    /// </summary>
    private bool RetryDue(Run run, PlanUnit unit, StepDecision decision, bool log = false)
    {
        var step = unit.Step;
        if (step.Retries is not > 0 || decision is not StepDecision.Schedule)
            return false;

        var failures = 0;
        foreach (var attempt in attempts.ListForStep(run.Id, unit.Name).Reverse())
        {
            if (attempt.Status is not Failed || (run.ResumedAt is { } resumed && attempt.CreatedAt < resumed))
                break;
            failures++;
        }

        if (failures == 0 || failures > step.Retries)
            return false;

        if (log)
            logger.LogInformation("Retrying step '{StepName}' in run #{RunNumber} ('{RunId}'): failure {Failures} of {MaxTries} tries",
                unit.Name, run.Number, run.Id, failures, step.Retries + 1);
        return true;
    }

    /// <summary>
    /// <c>cascadeFailure</c>: once a cell's last allowed attempt failed (since the run was last resumed, no retry due),
    /// the step's other cells that are scheduled or running are cancelled with <c>cancelled: &lt;label&gt; failed</c>.
    /// Only attempts created before that failure: a cell rerun afterwards runs.
    /// </summary>
    private void Cascade(Run run, PlanStage stage, StepDescriptor step, StepDecision decision)
    {
        if (decision is StepDecision.Excluded)
            return;

        var cells = stage.Units.Where(u => ReferenceEquals(u.Step, step)).ToList();
        var failures = cells
            .Select(u => (Unit: u, Latest: Latest(run.Id, u.Name)))
            .Where(c => c.Latest is { Status: Failed } latest
                        && !(run.ResumedAt is { } resumed && latest.CreatedAt < resumed)
                        && !RetryDue(run, c.Unit, decision))
            .ToList();
        if (failures.Count == 0)
            return;
        var source = failures.MinBy(c => ((Failed)c.Latest!.Status).FinishedAt);
        var failed = (Failed)source.Latest!.Status;

        var cancel = cells
            .Where(u => u != source.Unit)
            .Select(u => Latest(run.Id, u.Name))
            .OfType<Attempt>()
            .Where(a => a.Status.IsActive() && a.CreatedAt <= failed.FinishedAt)
            .ToList();
        if (cancel.Count > 0)
            cancellation.CancelCells(run, cancel, $"cancelled: {source.Unit.Cell!.Label} failed");
    }

    private void Act(Run run, PlanUnit unit, StepDecision decision)
    {
        var step = unit.Step;
        var name = unit.Name;
        switch (decision)
        {
            case StepDecision.Skip skip:
                if (attempts.RecordSkipped(run.Id, name, skip.Reason).TryPickProblems(out var skipProblems))
                    logger.LogProblems(LogLevel.Warning, skipProblems, "Could not record skip of step '{StepName}' in run #{RunNumber} ('{RunId}')",
                        name, run.Number, run.Id);
                else
                    logger.LogInformation("Skipped step '{StepName}' in run #{RunNumber} ('{RunId}'): {Reason}", name, run.Number, run.Id, skip.Reason);
                break;

            case StepDecision.Schedule schedule:
                if (attempts.Schedule(run.Id, name, schedule.Hash, schedule.Json, awaitsInput: step.ExternalInputs.Count > 0)
                    .TryPickProblems(out var scheduleProblems, out var attempt))
                {
                    logger.LogProblems(LogLevel.Warning, scheduleProblems, "Could not schedule step '{StepName}' in run #{RunNumber} ('{RunId}'); failing it",
                        name, run.Number, run.Id);
                    LogIfFailed(attempts.RecordFailed(run.Id, name, string.Join("; ", scheduleProblems.Select(p => p.FormattedMessage)), schedule.Hash), run, "record a failed attempt in");
                }
                else
                {
                    logger.LogInformation("Scheduled step '{StepName}' attempt {AttemptNumber} in run #{RunNumber} ('{RunId}')",
                        name, attempt.Number, run.Number, run.Id);
                }
                break;

            case StepDecision.Fail fail:
                logger.LogWarning("Failing step '{StepName}' in run #{RunNumber} ('{RunId}') without running it: {Reason}", name, run.Number, run.Id, fail.Reason);
                LogIfFailed(attempts.RecordFailed(run.Id, name, fail.Reason, fail.Hash), run, "record a failed attempt in");
                break;
        }
    }

    /// <returns>True when the stage ended the run.</returns>
    private bool ApplyStageOutcome(Run run, PlanStage stage)
    {
        var latest = stage.Units.Select(u => Latest(run.Id, u.Name)).OfType<Attempt>().ToList();

        if (latest.FirstOrDefault(a => a.Status is Failed) is { } failed)
        {
            logger.LogInformation("Run #{RunNumber} ('{RunId}') failed at step '{StepName}'", run.Number, run.Id, failed.StepName);
            LogIfFailed(runs.Complete(run.Id, RunStatus.Failed), run, "fail");
            return true;
        }

        if (latest.FirstOrDefault(a => a.Status is Cancelled) is { } cancelled)
        {
            logger.LogInformation("Run #{RunNumber} ('{RunId}') cancelled at step '{StepName}'", run.Number, run.Id, cancelled.StepName);
            LogIfFailed(runs.Complete(run.Id, RunStatus.Cancelled), run, "cancel");
            return true;
        }

        foreach (var attempt in latest)
        {
            if (attempt.Status is not Obsolete obsolete)
                continue;

            if (attempts.TryGet(obsolete.SupersedingAttemptId, out var winner) && winner.RunId != run.Id)
            {
                logger.LogInformation(
                    "Run #{RunNumber} ('{RunId}') superseded by run #{WinnerRunNumber}: its step '{StepName}' lost latest-wins",
                    run.Number, run.Id, winner.RunNumber, attempt.StepName);
                LogIfFailed(runs.Supersede(run.Id, winner.RunId), run, "supersede");
            }
            else
            {
                LogIfFailed(runs.Complete(run.Id, RunStatus.Cancelled), run, "cancel");
            }

            return true;
        }

        return false;
    }

    private void SetHeldAt(Run run, string? heldAt)
    {
        if (runs.TryGet(run.Id, out var current) && current.HeldAt != heldAt)
        {
            if (heldAt is not null)
                logger.LogInformation("Run #{RunNumber} ('{RunId}') held at blocked step '{StepName}'", run.Number, run.Id, heldAt);
            if (runs.SetHeldAt(run.Id, heldAt).TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Warning, problems, "Could not update the hold marker of run #{RunNumber} ('{RunId}')", run.Number, run.Id);
        }
    }

    private Attempt? Latest(Id<Run> runId, string stepName) => attempts.ListForStep(runId, stepName).LastOrDefault();

    internal static bool TryParseInput(string json, out JsonElement input)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            input = document.RootElement.Clone();
            return input.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            input = default;
            return false;
        }
    }

    private void LogIfFailed<T>(Result<T> result, Run run, string action)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not {Action} run #{RunNumber} ('{RunId}')", action, run.Number, run.Id);
    }
}

/// <summary>What the scheduler decides for a step.</summary>
internal abstract record StepDecision
{
    /// <summary>The step's <c>when</c> does not match the run input: it is not part of the run.</summary>
    public sealed record Excluded(string Reason) : StepDecision;
    public sealed record Skip(string Reason) : StepDecision;
    public sealed record Schedule(string Json, string Hash) : StepDecision;
    public sealed record Fail(string Reason, string Hash) : StepDecision;
}

/// <summary>A run's pool read live from the attempt store (skips recorded mid-walk are seen immediately).</summary>
internal sealed class RunPool(Id<Run> runId, JsonElement input, RunPlan plan, AttemptService attempts) : IRunPool
{
    private readonly Dictionary<Id<Attempt>, JsonElement?> _outputs = [];

    public JsonElement Input => input;

    public PoolEntry Get(string stepName)
    {
        if (plan.ExcludedReason(stepName, input) is { } excluded)
            return new PoolEntry.Excluded(excluded);
        if (plan.TryGetStep(stepName, out _, out var step) && step.Matrix is not null)
            return Cells(step);
        var latest = attempts.ListForStep(runId, stepName).LastOrDefault();
        if (latest?.Status is Skipped skipped)
            return new PoolEntry.Skipped(skipped.Reason);
        if (!attempts.TryGetLatestSucceeded(runId, stepName, out var succeeded))
            return new PoolEntry.Missing();

        return new PoolEntry.Produced(Output(succeeded));
    }

    /// <summary>A matrix step's cells: skipped cells are left out, and all of them skipped is the step skipped.</summary>
    private PoolEntry Cells(StepDescriptor step)
    {
        var outputs = new List<CellOutput>();
        var skipped = new List<string>();
        var units = PlanStage.UnitsOf(step).ToList();
        foreach (var unit in units)
        {
            if (attempts.ListForStep(runId, unit.Name).LastOrDefault()?.Status is Skipped s)
                skipped.Add(s.Reason);
            else if (!attempts.TryGetLatestSucceeded(runId, unit.Name, out var succeeded))
                return new PoolEntry.Missing();
            else if (Output(succeeded) is { } output)
                outputs.Add(new CellOutput(unit.Cell!, output));
        }

        return skipped.Count == units.Count ? new PoolEntry.Skipped(skipped[0]) : new PoolEntry.Cells(outputs);
    }

    private JsonElement? Output(Attempt attempt)
    {
        if (_outputs.TryGetValue(attempt.Id, out var cached))
            return cached;

        JsonElement? output = null;
        if (attempt.Output is { } o)
        {
            using var document = JsonDocument.Parse(o.Json);
            output = document.RootElement.Clone();
        }

        _outputs[attempt.Id] = output;
        return output;
    }
}
