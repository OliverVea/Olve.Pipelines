using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineDocumentBuilder(
    PipelineService pipelines,
    ProductionStepService productionSteps,
    ProcessingStepService processingSteps,
    TriggerService triggers,
    AttachmentStore<ProductionStep, StepConfiguration> productionConfigs,
    AttachmentStore<ProcessingStep, StepConfiguration> processingConfigs,
    EntityStore<ProcessingStep> processingStepStore)
{
    public Result<PipelineDocument> Build(Id<Pipeline> pipelineId)
    {
        if (!pipelines.TryGet(pipelineId, out var pipeline))
            return new ResultProblem($"Pipeline '{pipelineId}' not found.");

        var production = productionSteps.GetByPipelineId(pipelineId);
        if (production.TryPickProblems(out var productionProblems, out var productionArr))
            return productionProblems;

        var productionDocs = productionArr
            .Select(s => new ProductionStepDocument(s.Name, BuildStepConfig(productionConfigs, s.Id)))
            .ToList();

        var processing = processingSteps.GetByPipelineId(pipelineId);
        if (processing.TryPickProblems(out var processingProblems, out var processingArr))
            return processingProblems;

        var processingDocs = processingArr
            .OrderBy(s => s.Order)
            .Select(s => new ProcessingStepDocument(s.Name, BuildStepConfig(processingConfigs, s.Id)))
            .ToList();

        var triggerResult = triggers.GetByPipelineId(pipelineId);
        if (triggerResult.TryPickProblems(out var triggerProblems, out var triggerArr))
            return triggerProblems;

        var triggerDocs = new List<TriggerDocument>(triggerArr.Length);
        foreach (var trigger in triggerArr)
        {
            var targetResult = BuildTargetDocument(trigger.Target);
            if (targetResult.TryPickProblems(out var targetProblems, out var targetDoc))
                return targetProblems;

            triggerDocs.Add(new TriggerDocument(trigger.Name, targetDoc));
        }

        return new PipelineDocument(
            PipelineDocumentVersion.Current,
            pipeline.Name,
            productionDocs,
            processingDocs,
            triggerDocs);
    }

    private static StepConfigurationDocument? BuildStepConfig<TStep>(
        AttachmentStore<TStep, StepConfiguration> store,
        Id<TStep> stepId)
        where TStep : IHasId<Id<TStep>>
    {
        if (!store.TryGet(stepId, out var config))
            return null;

        return new StepConfigurationDocument(
            config.Image,
            config.Script,
            config.EnvironmentVariables);
    }

    private Result<TriggerTargetDocument> BuildTargetDocument(TriggerTarget target) => target switch
    {
        ProductionTriggerTarget => new ProductionTargetDocument(),
        ProcessingTriggerTarget processing => ResolveProcessingTarget(processing.ProcessingStepId),
        PollTriggerTarget poll => new PollTargetDocument(poll.Url, poll.Headers, poll.ValuePath, poll.IntervalSeconds),
        _ => new ResultProblem($"Unknown trigger target type '{target.GetType().Name}'."),
    };

    private Result<TriggerTargetDocument> ResolveProcessingTarget(Id<ProcessingStep> stepId)
    {
        if (!processingStepStore.TryGet(stepId, out var step))
            return new ResultProblem($"Trigger references processing step '{stepId}' which does not exist.");

        return new ProcessingTargetDocument(step.Name);
    }
}
