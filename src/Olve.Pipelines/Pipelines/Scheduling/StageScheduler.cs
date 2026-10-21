using System.Text;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Drives a run through its stages (spec §3.6, §5.2, §5.3). <see cref="Advance"/> is idempotent and
/// stateless: it re-derives everything from the store, walking the flattened stages from the start.
/// For each step of the first unfinished stage it decides:
/// <list type="number">
/// <item><b>when</b> — every enclosing condition must match the run input, else the step is skipped
/// (<c>when deployTo ∉ [prod]</c>);</item>
/// <item><b>inputs</b> — a required input referencing a skipped/unproduced output skips the step with a
/// chained reason; optional ones are left out;</item>
/// <item><b>gate</b> — a blocked step gets no attempt and holds the run (<see cref="Run.HeldAt"/>);</item>
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
    StepGateService gates,
    PipelineActivationService activations,
    RunSchedulingLock schedulingLock,
    ILogger<StageScheduler> logger)
{
    public void Advance(Id<Run> runId) => schedulingLock.Advance(runId, () => AdvanceOnce(runId));

    /// <summary>Advances every Running run of a pipeline, oldest first (gate changes, startup).</summary>
    public void AdvanceRunning(Id<Pipeline> pipelineId) => schedulingLock.Run(() =>
    {
        foreach (var run in runs.ListByPipeline(pipelineId).Where(r => r.Status is RunStatus.Running).OrderBy(r => r.Number))
            Advance(run.Id);
    });

    /// <summary>What the scheduler would do with a step right now (also used by reruns).</summary>
    internal static StepDecision Decide(PlanStage stage, StepDescriptor step, IRunPool pool)
    {
        if (stage.EvaluateWhen(pool.Input) is { } whenReason)
            return new StepDecision.Skip(whenReason);

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

        if (plans.Get(run.VersionId).TryPickProblems(out var planProblems, out var plan))
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

        var canStartWork = activations.TryGet(run.PipelineId, out var activation)
                           && activation is PipelineActivation.Active active
                           && active.VersionId == run.VersionId;
        var pool = new RunPool(run.Id, input, attempts);

        foreach (var stage in plan.Stages)
        {
            var waiting = false;
            string? heldAt = null;

            foreach (var step in stage.Steps)
            {
                var latest = Latest(run.Id, step.Name);
                if (latest is not null && latest.Status.IsActive())
                {
                    waiting = true;
                    continue;
                }

                var decision = Decide(stage, step, pool);
                if (IsSettled(latest, decision, run))
                    continue;

                if (decision is StepDecision.Schedule && gates.IsBlocked(run.PipelineId, step.Name))
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

                Act(run, step, decision);
                if (Latest(run.Id, step.Name) is { } created && created.Status.IsActive())
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

    private void Act(Run run, StepDescriptor step, StepDecision decision)
    {
        switch (decision)
        {
            case StepDecision.Skip skip:
                if (attempts.RecordSkipped(run.Id, step.Name, skip.Reason).TryPickProblems(out var skipProblems))
                    logger.LogProblems(LogLevel.Warning, skipProblems, "Could not record skip of step '{StepName}' in run #{RunNumber} ('{RunId}')",
                        step.Name, run.Number, run.Id);
                else
                    logger.LogInformation("Skipped step '{StepName}' in run #{RunNumber} ('{RunId}'): {Reason}", step.Name, run.Number, run.Id, skip.Reason);
                break;

            case StepDecision.Schedule schedule:
                if (attempts.Schedule(run.Id, step.Name, schedule.Hash, schedule.Json).TryPickProblems(out var scheduleProblems, out var attempt))
                {
                    logger.LogProblems(LogLevel.Warning, scheduleProblems, "Could not schedule step '{StepName}' in run #{RunNumber} ('{RunId}'); failing it",
                        step.Name, run.Number, run.Id);
                    LogIfFailed(attempts.RecordFailed(run.Id, step.Name, string.Join("; ", scheduleProblems.Select(p => p.Message)), schedule.Hash), run, "record a failed attempt in");
                }
                else
                {
                    logger.LogInformation("Scheduled step '{StepName}' attempt {AttemptNumber} in run #{RunNumber} ('{RunId}')",
                        step.Name, attempt.Number, run.Number, run.Id);
                }
                break;

            case StepDecision.Fail fail:
                logger.LogWarning("Failing step '{StepName}' in run #{RunNumber} ('{RunId}') without running it: {Reason}", step.Name, run.Number, run.Id, fail.Reason);
                LogIfFailed(attempts.RecordFailed(run.Id, step.Name, fail.Reason, fail.Hash), run, "record a failed attempt in");
                break;
        }
    }

    /// <returns>True when the stage ended the run.</returns>
    private bool ApplyStageOutcome(Run run, PlanStage stage)
    {
        var latest = stage.Steps.Select(s => Latest(run.Id, s.Name)).OfType<Attempt>().ToList();

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
                logger.LogInformation("Run #{RunNumber} ('{RunId}') held at gated step '{StepName}'", run.Number, run.Id, heldAt);
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
    public sealed record Skip(string Reason) : StepDecision;
    public sealed record Schedule(string Json, string Hash) : StepDecision;
    public sealed record Fail(string Reason, string Hash) : StepDecision;
}

/// <summary>A run's pool read live from the attempt store (skips recorded mid-walk are seen immediately).</summary>
internal sealed class RunPool(Id<Run> runId, JsonElement input, AttemptService attempts) : IRunPool
{
    private readonly Dictionary<Id<Attempt>, JsonElement?> _outputs = [];

    public JsonElement Input => input;

    public PoolEntry Get(string stepName)
    {
        var latest = attempts.ListForStep(runId, stepName).LastOrDefault();
        if (latest?.Status is Skipped skipped)
            return new PoolEntry.Skipped(skipped.Reason);
        if (!attempts.TryGetLatestSucceeded(runId, stepName, out var succeeded))
            return new PoolEntry.Missing();

        return new PoolEntry.Produced(Output(succeeded));
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
