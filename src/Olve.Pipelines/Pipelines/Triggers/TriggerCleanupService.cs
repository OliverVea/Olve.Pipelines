using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Triggers;

public class TriggerCleanupService(EntityStore<Trigger> store)
{
    private readonly EntityStoreIndex<Trigger, Id<Pipeline>> _byPipeline = store.CreateIndex(t => t.PipelineId);

    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
            store.Delete(id);
    }
}
