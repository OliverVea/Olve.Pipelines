using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingCleanupService(EntityStore<PipelineConfigBinding> store)
{
    private readonly EntityStoreIndex<PipelineConfigBinding, Id<Pipeline>> _byPipeline =
        store.CreateIndex(b => b.PipelineId);

    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
            store.Delete(id).DiscardResult(); // cascade delete; NotFound (already gone) is benign
    }
}
