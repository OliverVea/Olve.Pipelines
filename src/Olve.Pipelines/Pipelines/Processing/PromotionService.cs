using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Building;

namespace Olve.Pipelines.Pipelines.Processing;

/// <summary>Where a step's promotions come from: the previous processing step, or production for the first step.</summary>
public record PromotionSource(Id<ProcessingStep>? ProcessingStepId, string Name)
{
    public bool IsProduction => ProcessingStepId is null;
}

/// <summary>
/// What an action would do right now. <see cref="Allowed"/> with <see cref="Bundle"/> = it would run the
/// step with that bundle; otherwise <see cref="Reason"/> (a <see cref="PromotionRefusal"/> name, camelCased)
/// says why not. <see cref="Message"/> is the human-readable sentence either way.
/// </summary>
public record PromotionOption(bool Allowed, PromotionBundle? Bundle, string? Reason, string Message);

/// <summary>
/// A processing step's promotion gate plus what Promote (and unblocking, when blocked) would use now.
/// <see cref="OnUnblock"/> is null when the step is not blocked.
/// </summary>
public record StepPromotionState(
    Id<ProcessingStep> ProcessingStepId,
    string StepName,
    bool Blocked,
    PromotionSource Source,
    PromotionBundle? SourceLastSuccess,
    PromotionBundle? LastSuccess,
    PromotionOption Promote,
    PromotionOption? OnUnblock);

/// <summary>The promotion check an unblock ran: whether it started one, and with which bundle.</summary>
public record UnblockPromotion(bool Started, Id<JobGroup>? JobGroupId, PromotionBundle? Bundle, string? Reason, string Message);

/// <summary>Response of setting the gate. <see cref="Promotion"/> is set on unblock, null on block.</summary>
public record PromotionState(bool Blocked, UnblockPromotion? Promotion);

/// <summary>A promotion Promote started.</summary>
public record PromoteResult(
    Id<JobGroup> JobGroupId,
    Id<ProcessingStep> ProcessingStepId,
    PromotionBundle Bundle,
    PromotionSource Source,
    string Message);

/// <summary>
/// Promote, the unblock check, and the "what would Promote use" read model. Gathers
/// <see cref="PromotionFacts"/> from the gate, the step configuration and the persisted job history,
/// and leaves the decision to <see cref="PromotionPolicy"/>. Every decision that can create a run is
/// taken under the pipeline's <see cref="PromotionLocks"/> lock, and the run is created through
/// <see cref="JobService.CreateProcessingRun"/>.
/// </summary>
public class PromotionService(
    ProcessingStepService steps,
    PromotionGateService gate,
    JobService jobs,
    JobGroupService jobGroups,
    PromotionLocks locks,
    ILogger<PromotionService> logger)
{
    private record Context(ProcessingStep Step, PromotionSource Source, PromotionFacts Facts, PromotionHistory History);

    public Result<StepPromotionState> GetState(Id<ProcessingStep> stepId)
    {
        if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
            return problems;

        if (steps.GetByPipelineId(step.PipelineId).TryPickProblems(out problems, out var pipelineSteps))
            return problems;

        var history = HistoryOf(step.PipelineId);
        return Gather(step, pipelineSteps, history) is { } ctx
            ? StateOf(ctx)
            : new ResultProblem($"Processing step '{stepId}' is not part of its pipeline's step list.");
    }

    public Result<StepPromotionState[]> ListStates(Id<Pipeline> pipelineId)
    {
        if (steps.GetByPipelineId(pipelineId).TryPickProblems(out var problems, out var pipelineSteps))
            return problems;

        var history = HistoryOf(pipelineId);
        return pipelineSteps
            .Select(s => Gather(s, pipelineSteps, history))
            .OfType<Context>()
            .Select(StateOf)
            .ToArray();
    }

    /// <summary>
    /// Runs the step with the bundle that last succeeded at the step before it. When
    /// <paramref name="expectedBundleId"/> is given (the bundle the caller was shown), refuses if the
    /// server would now use a different one.
    /// </summary>
    public Result<PromoteResult> Promote(Id<ProcessingStep> stepId, Id<ArtifactBundle>? expectedBundleId)
    {
        if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
            return problems;

        lock (locks.For(step.PipelineId))
        {
            if (Load(step).TryPickProblems(out problems, out var ctx))
                return problems;

            var decision = PromotionPolicy.Decide(PromotionIntent.Promote, ctx.Facts);
            if (decision.Bundle is not { } bundle)
                return new ResultProblem($"Promote refused: {Explain(PromotionIntent.Promote, decision, ctx)}");

            if (expectedBundleId is { } expected && expected != bundle.ArtifactBundleId)
                return new ResultProblem(
                    $"Promote refused: you confirmed bundle {Short(expected)}, but the last success at {SourceLabel(ctx.Source)} is now {Describe(bundle)}. Refresh and try again.");

            var group = jobs.CreateProcessingRun(step.PipelineId, bundle.ArtifactBundleId, step.Id);
            logger.LogInformation(
                "Promoted bundle '{BundleId}' (built {BuiltAt:o}) from '{SourceName}' into processing step '{StepName}' ({StepId}) of pipeline '{PipelineId}'",
                bundle.ArtifactBundleId, bundle.BuiltAt, ctx.Source.Name, step.Name, step.Id, step.PipelineId);

            return new PromoteResult(group.Id, step.Id, bundle, ctx.Source,
                $"Promoted {Describe(bundle)} from {SourceLabel(ctx.Source)} into '{step.Name}'.");
        }
    }

    /// <summary>
    /// Sets the gate. Unblocking a blocked step then checks whether a promotion is due (the previous
    /// step's last success is newer than what this step last ran successfully) and starts it.
    /// Unblocking a step that was not blocked changes nothing and starts nothing.
    /// </summary>
    public Result<PromotionState> SetBlocked(Id<ProcessingStep> stepId, bool blocked)
    {
        if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
            return problems;

        lock (locks.For(step.PipelineId))
        {
            if (blocked)
            {
                gate.Block(stepId);
                return new PromotionState(true, null);
            }

            var wasBlocked = gate.IsBlocked(stepId);
            gate.Unblock(stepId);

            if (!wasBlocked)
                return new PromotionState(false, new UnblockPromotion(false, null, null, "notBlocked",
                    $"'{step.Name}' was not blocked; no promotion check was run."));

            if (Load(step).TryPickProblems(out problems, out var ctx))
                return new PromotionState(false, new UnblockPromotion(false, null, null, "unknownStep",
                    $"Unblocked '{step.Name}', but could not check for a due promotion: {string.Join("; ", problems.Select(p => p.Message))}"));

            var decision = PromotionPolicy.Decide(PromotionIntent.Unblock, ctx.Facts);
            if (decision.Bundle is not { } bundle)
                return new PromotionState(false, new UnblockPromotion(false, null, null, ReasonOf(decision),
                    $"Unblocked '{step.Name}'; no promotion started: {Explain(PromotionIntent.Unblock, decision, ctx)}"));

            var group = jobs.CreateProcessingRun(step.PipelineId, bundle.ArtifactBundleId, step.Id);
            logger.LogInformation(
                "Unblocked processing step '{StepName}' ({StepId}) of pipeline '{PipelineId}' and promoted bundle '{BundleId}' (built {BuiltAt:o}) from '{SourceName}'",
                step.Name, step.Id, step.PipelineId, bundle.ArtifactBundleId, bundle.BuiltAt, ctx.Source.Name);

            return new PromotionState(false, new UnblockPromotion(true, group.Id, bundle, null,
                $"Unblocked '{step.Name}' and started promotion of {Describe(bundle)} from {SourceLabel(ctx.Source)}."));
        }
    }

    /// <summary>The bundles the promotion logic may still need for one pipeline (see <see cref="PromotionHistory.BundlesInUse"/>).</summary>
    public IEnumerable<Id<ArtifactBundle>> BundlesInUse(Id<Pipeline> pipelineId) => HistoryOf(pipelineId).BundlesInUse();

    private PromotionHistory HistoryOf(Id<Pipeline> pipelineId)
        => PromotionHistory.Build(jobs.GetJobsByPipeline(pipelineId), id => jobGroups.TryGet(id, out var g) ? g : null);

    private Result<Context> Load(ProcessingStep step)
    {
        if (steps.GetByPipelineId(step.PipelineId).TryPickProblems(out var problems, out var pipelineSteps))
            return problems;

        return Gather(step, pipelineSteps, HistoryOf(step.PipelineId)) is { } ctx
            ? ctx
            : new ResultProblem($"Processing step '{step.Id}' is not part of its pipeline's step list.");
    }

    private Context? Gather(ProcessingStep step, ProcessingStep[] pipelineSteps, PromotionHistory history)
    {
        var index = Array.FindIndex(pipelineSteps, s => s.Id == step.Id);
        if (index < 0)
            return null;

        PromotionSource source;
        Id<ArtifactBundle>? sourceSuccess;
        if (index == 0)
        {
            source = new PromotionSource(null, "production");
            sourceSuccess = history.LatestProductionSuccess;
        }
        else
        {
            var previous = pipelineSteps[index - 1];
            source = new PromotionSource(previous.Id, previous.Name);
            sourceSuccess = history.LastSuccessOf(previous.Id);
        }

        var lastSuccess = history.LastSuccessOf(step.Id);
        var facts = new PromotionFacts(
            Blocked: gate.IsBlocked(step.Id),
            Configured: !steps.TryGetConfiguration(step.Id).TryPickProblems(out _),
            SourceSuccess: sourceSuccess,
            SourceSuccessBuilt: sourceSuccess is { } s ? history.SucceededBuildOf(s) : null,
            LastSuccess: lastSuccess,
            LastSuccessBuilt: lastSuccess is { } l ? history.BuildOf(l) : null,
            HasActiveJob: history.ActiveJobsOf(step.Id).Count > 0);

        return new Context(step, source, facts, history);
    }

    private static StepPromotionState StateOf(Context ctx)
    {
        var promote = OptionOf(PromotionIntent.Promote, ctx, ctx.Facts);
        var onUnblock = ctx.Facts.Blocked
            ? OptionOf(PromotionIntent.Unblock, ctx, ctx.Facts with { Blocked = false })
            : null;

        return new StepPromotionState(
            ctx.Step.Id,
            ctx.Step.Name,
            ctx.Facts.Blocked,
            ctx.Source,
            ctx.Facts.SourceSuccessBuilt,
            ctx.Facts.LastSuccessBuilt,
            promote,
            onUnblock);
    }

    private static PromotionOption OptionOf(PromotionIntent intent, Context ctx, PromotionFacts facts)
    {
        var decision = PromotionPolicy.Decide(intent, facts);
        return decision.Bundle is { } bundle
            ? new PromotionOption(true, bundle, null, $"Promote {Describe(bundle)} from {SourceLabel(ctx.Source)}")
            : new PromotionOption(false, null, ReasonOf(decision), Capitalize(Explain(intent, decision, ctx with { Facts = facts })));
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string ReasonOf(PromotionDecision decision)
    {
        var name = decision.Refusal!.Value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string Explain(PromotionIntent intent, PromotionDecision decision, Context ctx)
    {
        var step = ctx.Step.Name;
        var source = SourceLabel(ctx.Source);
        var f = ctx.Facts;
        return decision.Refusal switch
        {
            PromotionRefusal.Blocked => $"promotion into '{step}' is blocked; unblock it first.",
            PromotionRefusal.NotConfigured => $"'{step}' has no configuration.",
            PromotionRefusal.NothingPassedSource => ctx.Source.IsProduction
                ? "no production run has completed yet."
                : $"nothing has succeeded at {source} yet.",
            PromotionRefusal.BundleGone =>
                $"the bundle that last succeeded at {source} (bundle {Short(f.SourceSuccess!.Value)}) is no longer available: its build record is gone or its production run did not complete.",
            PromotionRefusal.JobActive => $"a job for '{step}' is already scheduled or in progress; wait for it or cancel it.",
            PromotionRefusal.AlreadyRan => $"'{step}' already ran {Describe(f.SourceSuccessBuilt!)}, the last success at {source}.",
            PromotionRefusal.WouldDowngrade =>
                $"the last success at {source} ({Describe(f.SourceSuccessBuilt!)}) is older than what '{step}' last ran ({Describe(f.LastSuccessBuilt!)}); promotion never goes backwards.",
            PromotionRefusal.UnknownOrder =>
                $"'{step}' last ran bundle {Short(f.LastSuccess!.Value)}, which has no build record, so it cannot be told whether the last success at {source} is newer.",
            _ => intent == PromotionIntent.Promote ? "nothing to promote." : "no promotion due.",
        };
    }

    private static string SourceLabel(PromotionSource source)
        => source.IsProduction ? "production" : $"'{source.Name}'";

    internal static string Short(Id<ArtifactBundle> id) => id.Value.Value.ToString("N")[..8];

    internal static string Describe(PromotionBundle bundle)
        => $"the build of {bundle.BuiltAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC (bundle {Short(bundle.ArtifactBundleId)})";
}
