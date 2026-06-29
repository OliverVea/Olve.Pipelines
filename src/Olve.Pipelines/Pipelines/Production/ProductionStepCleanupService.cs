using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public class ProductionStepCleanupService(EntityStore<ProductionStep> store)
{
    private readonly EntityStoreIndex<ProductionStep, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);

    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
            _ = store.Delete(id); // cascade delete; NotFound (already gone) is benign
    }
}
