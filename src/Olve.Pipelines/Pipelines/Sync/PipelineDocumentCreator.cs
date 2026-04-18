using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineDocumentCreator(
    PipelineService pipelines,
    ProductionStepService productionSteps,
    ProcessingStepService processingSteps,
    TriggerService triggers,
    PipelineDocumentBuilder documentBuilder)
{
    public Result<PipelineDocument> Create(PipelineDocument document)
    {
        var versionCheck = PipelineDocumentVersion.EnsureCompatible(document.ApiVersion);
        if (versionCheck.TryPickProblems(out var versionProblems))
            return versionProblems;

        var validation = ValidateInternalReferences(document);
        if (validation.TryPickProblems(out var validationProblems))
            return validationProblems;

        var pipelineResult = pipelines.Create(document.Name);
        if (pipelineResult.TryPickProblems(out var pipelineProblems, out var pipeline))
            return pipelineProblems;

        foreach (var stepDoc in document.ProductionSteps)
        {
            var stepResult = productionSteps.Create(pipeline.Id, stepDoc.Name);
            if (stepResult.TryPickProblems(out var stepProblems, out var step))
                return stepProblems;

            if (stepDoc.Configuration is { } config)
            {
                var configResult = productionSteps.SetConfiguration(step.Id, ToStepConfiguration(config));
                if (configResult.TryPickProblems(out var configProblems))
                    return configProblems;
            }
        }

        var processingByName = new Dictionary<string, Id<ProcessingStep>>(StringComparer.Ordinal);
        for (var order = 0; order < document.ProcessingSteps.Count; order++)
        {
            var stepDoc = document.ProcessingSteps[order];
            var stepResult = processingSteps.Create(pipeline.Id, stepDoc.Name, order);
            if (stepResult.TryPickProblems(out var stepProblems, out var step))
                return stepProblems;

            processingByName[stepDoc.Name] = step.Id;

            if (stepDoc.Configuration is { } config)
            {
                var configResult = processingSteps.SetConfiguration(step.Id, ToStepConfiguration(config));
                if (configResult.TryPickProblems(out var configProblems))
                    return configProblems;
            }
        }

        foreach (var triggerDoc in document.Triggers)
        {
            var targetResult = ResolveTarget(triggerDoc.Target, processingByName);
            if (targetResult.TryPickProblems(out var targetProblems, out var target))
                return targetProblems;

            var triggerResult = triggers.Create(pipeline.Id, triggerDoc.Name, target);
            if (triggerResult.TryPickProblems(out var triggerProblems))
                return triggerProblems;
        }

        return documentBuilder.Build(pipeline.Id);
    }

    private static Result ValidateInternalReferences(PipelineDocument document)
    {
        var processingNames = new HashSet<string>(
            document.ProcessingSteps.Select(s => s.Name), StringComparer.Ordinal);

        foreach (var trigger in document.Triggers)
        {
            if (trigger.Target is ProcessingTargetDocument processingTarget
                && !processingNames.Contains(processingTarget.ProcessingStepName))
            {
                return new ResultProblem(
                    $"Trigger '{trigger.Name}' references unknown processing step '{processingTarget.ProcessingStepName}'.");
            }
        }

        return Result.Success();
    }

    private static StepConfiguration ToStepConfiguration(StepConfigurationDocument config)
        => new(
            config.Image,
            config.Script,
            config.EnvironmentVariables?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

    private static Result<TriggerTarget> ResolveTarget(
        TriggerTargetDocument target,
        IReadOnlyDictionary<string, Id<ProcessingStep>> processingByName) => target switch
    {
        ProductionTargetDocument => new ProductionTriggerTarget(),
        ProcessingTargetDocument processing => processingByName.TryGetValue(processing.ProcessingStepName, out var id)
            ? new ProcessingTriggerTarget(id)
            : new ResultProblem($"Unknown processing step '{processing.ProcessingStepName}'."),
        PollTargetDocument poll => new PollTriggerTarget(
            poll.Url,
            poll.Headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            poll.ValuePath,
            poll.IntervalSeconds),
        _ => new ResultProblem($"Unknown trigger target type '{target.GetType().Name}'."),
    };
}
