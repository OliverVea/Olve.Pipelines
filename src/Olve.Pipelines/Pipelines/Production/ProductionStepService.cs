using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public class ProductionStepService(
    EntityStore<ProductionStep> store,
    AttachmentStore<ProductionStep, StepConfiguration> configuration,
    IdProvider idProvider)
{
    private readonly EntityStoreIndex<ProductionStep, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);

    public Result<ProductionStep> Create(Id<Pipeline> pipelineId, string name)
    {
        var step = new ProductionStep(idProvider.Create<ProductionStep>(), name, pipelineId);
        store.Set(step);
        return step;
    }

    public Result<ProductionStep> TryGet(Id<ProductionStep> id)
        => store.TryGet(id, out var step)
            ? step
            : Result.Failure<ProductionStep>(new ResultProblem($"Production step '{id}' not found."));

    public Result<ProductionStep[]> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<ProductionStep>(ids.Count);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var step))
                results.Add(step);
        }
        return results.ToArray();
    }

    public DeletionResult Delete(Id<ProductionStep> id) => store.Delete(id);

    public Result<StepConfiguration> SetConfiguration(Id<ProductionStep> id, StepConfiguration config)
    {
        if (!store.TryGet(id, out _))
            return Result.Failure<StepConfiguration>(new ResultProblem($"Production step '{id}' not found."));

        configuration.Set(id, config);
        return config;
    }

    public Result<StepConfiguration> TryGetConfiguration(Id<ProductionStep> id)
        => configuration.TryGet(id, out var config)
            ? config
            : Result.Failure<StepConfiguration>(new ResultProblem($"Production step '{id}' has no configuration."));

    public Result RemoveConfiguration(Id<ProductionStep> id)
    {
        if (!configuration.Remove(id))
            return Result.Failure(new ResultProblem($"Production step '{id}' has no configuration."));

        return Result.Success();
    }
}
