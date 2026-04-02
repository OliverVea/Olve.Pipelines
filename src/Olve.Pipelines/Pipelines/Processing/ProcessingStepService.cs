using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public class ProcessingStepService(
    EntityStore<ProcessingStep> store,
    AttachmentStore<ProcessingStep, StepConfiguration> configuration,
    IdProvider idProvider)
{
    private readonly EntityStoreIndex<ProcessingStep, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);

    public Result<ProcessingStep> Create(Id<Pipeline> pipelineId, string name, int order)
    {
        var step = new ProcessingStep(idProvider.Create<ProcessingStep>(), name, pipelineId, order);
        store.Set(step);
        return step;
    }

    public Result<ProcessingStep> TryGet(Id<ProcessingStep> id)
        => store.TryGet(id, out var step)
            ? step
            : Result.Failure<ProcessingStep>(new ResultProblem($"Processing step '{id}' not found."));

    public Result<ProcessingStep[]> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<ProcessingStep>(ids.Count);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var step))
                results.Add(step);
        }
        return results.OrderBy(s => s.Order).ToArray();
    }

    public DeletionResult Delete(Id<ProcessingStep> id) => store.Delete(id);

    public Result<ProcessingStep> UpdateOrder(Id<ProcessingStep> id, int newOrder)
    {
        if (!store.TryGet(id, out var step))
            return Result.Failure<ProcessingStep>(new ResultProblem($"Processing step '{id}' not found."));

        var updated = step with { Order = newOrder };
        store.Set(updated);
        return updated;
    }

    public Result<StepConfiguration> SetConfiguration(Id<ProcessingStep> id, StepConfiguration config)
    {
        if (!store.TryGet(id, out _))
            return Result.Failure<StepConfiguration>(new ResultProblem($"Processing step '{id}' not found."));

        configuration.Set(id, config);
        return config;
    }

    public Result<StepConfiguration> TryGetConfiguration(Id<ProcessingStep> id)
        => configuration.TryGet(id, out var config)
            ? config
            : Result.Failure<StepConfiguration>(new ResultProblem($"Processing step '{id}' has no configuration."));

    public Result RemoveConfiguration(Id<ProcessingStep> id)
    {
        if (!configuration.Remove(id))
            return Result.Failure(new ResultProblem($"Processing step '{id}' has no configuration."));

        return Result.Success();
    }
}
