using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingService(
    EntityStore<PipelineConfigBinding> store,
    IdProvider idProvider)
{
    private readonly EntityStoreIndex<PipelineConfigBinding, Id<Pipeline>> _byPipeline =
        store.CreateIndex(b => b.PipelineId);

    public Result<PipelineConfigBinding> Create(Id<Pipeline> pipelineId)
    {
        var binding = new PipelineConfigBinding(
            idProvider.Create<PipelineConfigBinding>(),
            pipelineId,
            DateTimeOffset.UtcNow);

        store.Set(binding);
        return binding;
    }

    public Result<PipelineConfigBinding> TryGet(Id<PipelineConfigBinding> id)
        => store.TryGet(id, out var binding)
            ? binding
            : Result.Failure<PipelineConfigBinding>(new ResultProblem($"Binding '{id}' not found."));

    public Result<PipelineConfigBinding> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var binding))
                return binding;
        }

        return Result.Failure<PipelineConfigBinding>(new ResultProblem($"Pipeline '{pipelineId}' has no config binding."));
    }

    public DeletionResult Delete(Id<PipelineConfigBinding> id) => store.Delete(id);
}
