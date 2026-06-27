using Olve.Pipelines.FailureHandlers;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Converges a pipeline's live configuration to a desired <see cref="PipelineManifest"/> via a
/// name-keyed add/update/delete diff (spec Approach B). It is the sole writer of config entities
/// during reconcile and runs synchronously: callers (the reconcile coordinator) guarantee the
/// pipeline is quiescent and hold the global config lock around this call.
///
/// Matching is by NAME, so a step that survives a reconcile keeps its Id (and therefore its job
/// history). A rename is a delete + recreate. Triggers are delete-to-match — safe because the
/// deploy "trigger" is the binding-derived branch poll, not a <see cref="Trigger"/> entity.
/// </summary>
public class PipelineReconciler(
    PipelineService pipelines,
    ProductionStepService productionSteps,
    ProcessingStepService processingSteps,
    TriggerService triggers,
    FailureHandlerBindingService failureHandlers)
{
    public Result Reconcile(Id<Pipeline> pipelineId, PipelineManifest desired)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return new ResultProblem($"Pipeline '{pipelineId}' not found.");

        // The name is GitOps config too: the bind seeds a provisional name from the repo, and the
        // manifest's name takes over from the first reconcile on. (No-op when already matching.)
        if (pipelines.SetName(pipelineId, desired.Name).TryPickProblems(out var nameProblems))
            return nameProblems;

        if (ReconcileProductionSteps(pipelineId, desired).TryPickProblems(out var prodProblems))
            return prodProblems;

        if (ReconcileProcessingSteps(pipelineId, desired).TryPickProblems(out var procProblems))
            return procProblems;

        if (ReconcileTriggers(pipelineId, desired).TryPickProblems(out var triggerProblems))
            return triggerProblems;

        ReconcileFailureHandlers(pipelineId, desired);

        return Result.Success();
    }

    private void ReconcileFailureHandlers(Id<Pipeline> pipelineId, PipelineManifest desired)
    {
        // Bindings reference steps by name (like the config itself), so no resolution against live
        // step Ids is needed — the set is rewritten wholesale, and an empty list clears it.
        var bindings = (desired.FailureHandlers ?? [])
            .Select(h => new FailureHandlerBinding(
                h.Handler,
                h.Steps ?? [],
                h.Env ?? new Dictionary<string, string>()))
            .ToList();

        failureHandlers.Set(pipelineId, bindings);
    }

    private Result ReconcileProductionSteps(Id<Pipeline> pipelineId, PipelineManifest desired)
    {
        var liveResult = productionSteps.GetByPipelineId(pipelineId);
        if (liveResult.TryPickProblems(out var problems, out var live))
            return problems;

        var liveByName = live.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var desiredSteps = desired.ProductionSteps ?? [];
        var desiredNames = new HashSet<string>(desiredSteps.Select(s => s.Name), StringComparer.Ordinal);

        foreach (var step in desiredSteps)
        {
            var desiredConfig = ToConfiguration(step.Configuration);

            if (liveByName.TryGetValue(step.Name, out var existing))
            {
                if (ApplyConfiguration(
                        desiredConfig,
                        productionSteps.TryGetConfiguration(existing.Id),
                        config => productionSteps.SetConfiguration(existing.Id, config).ToEmptyResult(),
                        () => productionSteps.RemoveConfiguration(existing.Id))
                    .TryPickProblems(out var configProblems))
                    return configProblems;

                continue;
            }

            var createResult = productionSteps.Create(pipelineId, step.Name);
            if (createResult.TryPickProblems(out var createProblems, out var created))
                return createProblems;

            if (desiredConfig is not null
                && productionSteps.SetConfiguration(created.Id, desiredConfig).TryPickProblems(out var setProblems))
                return setProblems;
        }

        foreach (var step in live)
        {
            if (!desiredNames.Contains(step.Name))
                productionSteps.Delete(step.Id);
        }

        return Result.Success();
    }

    private Result ReconcileProcessingSteps(Id<Pipeline> pipelineId, PipelineManifest desired)
    {
        var liveResult = processingSteps.GetByPipelineId(pipelineId);
        if (liveResult.TryPickProblems(out var problems, out var live))
            return problems;

        var liveByName = live.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var desiredSteps = desired.ProcessingSteps ?? [];
        var desiredNames = new HashSet<string>(desiredSteps.Select(s => s.Name), StringComparer.Ordinal);

        // List index is the order (spec decision: no explicit order field).
        for (var order = 0; order < desiredSteps.Count; order++)
        {
            var step = desiredSteps[order];
            var desiredConfig = ToConfiguration(step.Configuration);

            if (liveByName.TryGetValue(step.Name, out var existing))
            {
                if (existing.Order != order
                    && processingSteps.UpdateOrder(existing.Id, order).TryPickProblems(out var orderProblems))
                    return orderProblems;

                if (ApplyConfiguration(
                        desiredConfig,
                        processingSteps.TryGetConfiguration(existing.Id),
                        config => processingSteps.SetConfiguration(existing.Id, config).ToEmptyResult(),
                        () => processingSteps.RemoveConfiguration(existing.Id))
                    .TryPickProblems(out var configProblems))
                    return configProblems;

                continue;
            }

            var createResult = processingSteps.Create(pipelineId, step.Name, order);
            if (createResult.TryPickProblems(out var createProblems, out var created))
                return createProblems;

            if (desiredConfig is not null
                && processingSteps.SetConfiguration(created.Id, desiredConfig).TryPickProblems(out var setProblems))
                return setProblems;
        }

        foreach (var step in live)
        {
            if (!desiredNames.Contains(step.Name))
                processingSteps.Delete(step.Id);
        }

        return Result.Success();
    }

    private Result ReconcileTriggers(Id<Pipeline> pipelineId, PipelineManifest desired)
    {
        // Resolve targets against the now-current processing steps (after step reconcile).
        var processingResult = processingSteps.GetByPipelineId(pipelineId);
        if (processingResult.TryPickProblems(out var processingProblems, out var processing))
            return processingProblems;

        var processingByName = processing.ToDictionary(s => s.Name, s => s.Id, StringComparer.Ordinal);

        var liveResult = triggers.GetByPipelineId(pipelineId);
        if (liveResult.TryPickProblems(out var problems, out var live))
            return problems;

        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var desiredTriggers = desired.Triggers ?? [];
        var desiredNames = new HashSet<string>(desiredTriggers.Select(t => t.Name), StringComparer.Ordinal);

        foreach (var trigger in desiredTriggers)
        {
            var targetResult = ResolveTarget(trigger.Target, processingByName);
            if (targetResult.TryPickProblems(out var targetProblems, out var target))
                return targetProblems;

            if (liveByName.TryGetValue(trigger.Name, out var existing))
            {
                if (TargetEquals(existing.Target, target))
                    continue;

                triggers.Delete(existing.Id); // target changed — recreate (Id is not history-bearing)
            }

            if (triggers.Create(pipelineId, trigger.Name, target).TryPickProblems(out var createProblems))
                return createProblems;
        }

        foreach (var trigger in live)
        {
            if (!desiredNames.Contains(trigger.Name))
                triggers.Delete(trigger.Id);
        }

        return Result.Success();
    }

    /// <summary>Sets, replaces, or removes a step's configuration to match the desired state.</summary>
    private static Result ApplyConfiguration(
        StepConfiguration? desired,
        Result<StepConfiguration> current,
        Func<StepConfiguration, Result> set,
        Func<Result> remove)
    {
        var hasCurrent = !current.TryPickProblems(out _, out var currentConfig);

        if (desired is null)
            return hasCurrent ? remove() : Result.Success();

        if (hasCurrent && currentConfig is not null && ConfigEquals(currentConfig, desired))
            return Result.Success();

        return set(desired);
    }

    private static StepConfiguration? ToConfiguration(StepConfigurationDocument? document)
        => document is null
            ? null
            : new StepConfiguration(
                document.Image,
                document.Script,
                document.EnvironmentVariables?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

    private static Result<TriggerTarget> ResolveTarget(
        TriggerTargetDocument target, IReadOnlyDictionary<string, Id<ProcessingStep>> processingByName) => target switch
    {
        ProductionTargetDocument => new ProductionTriggerTarget(),
        ProcessingTargetDocument processing => processingByName.TryGetValue(processing.ProcessingStepName, out var id)
            ? new ProcessingTriggerTarget(id)
            : new ResultProblem($"Trigger references unknown processing step '{processing.ProcessingStepName}'."),
        PollTargetDocument poll => new PollTriggerTarget(
            poll.Url,
            poll.Headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            poll.ValuePath,
            poll.IntervalSeconds),
        GitHubTargetDocument gh => new GitHubWebhookTarget(gh.Owner, gh.Repo, gh.Branch, gh.TokenSecret),
        _ => new ResultProblem($"Unknown trigger target type '{target.GetType().Name}'."),
    };

    private static bool ConfigEquals(StepConfiguration a, StepConfiguration b)
        => a.Image == b.Image && a.Script == b.Script && DictionaryEquals(a.EnvironmentVariables, b.EnvironmentVariables);

    private static bool TargetEquals(TriggerTarget a, TriggerTarget b) => (a, b) switch
    {
        (ProductionTriggerTarget, ProductionTriggerTarget) => true,
        (ProcessingTriggerTarget x, ProcessingTriggerTarget y) => x.ProcessingStepId == y.ProcessingStepId,
        (PollTriggerTarget x, PollTriggerTarget y) =>
            x.Url == y.Url && x.ValuePath == y.ValuePath && x.IntervalSeconds == y.IntervalSeconds
            && DictionaryEquals(x.Headers, y.Headers),
        (GitHubWebhookTarget x, GitHubWebhookTarget y) =>
            x.Owner == y.Owner && x.Repo == y.Repo && x.Branch == y.Branch
            && x.TokenSecretName == y.TokenSecretName,
        _ => false,
    };

    private static bool DictionaryEquals(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB)
            return false;
        if (countA == 0)
            return true;

        foreach (var (key, value) in a!)
        {
            if (!b!.TryGetValue(key, out var other) || other != value)
                return false;
        }

        return true;
    }
}
