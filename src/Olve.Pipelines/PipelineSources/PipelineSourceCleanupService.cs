using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.PipelineSources;

public class PipelineSourceCleanupService(EntityStore<PipelineSource> store)
{
    private readonly EntityStoreIndex<PipelineSource, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);

    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        var sourceIds = _byPipeline.GetForKey(pipelineId).ToList();
        foreach (var id in sourceIds)
        {
            store.Delete(id);
        }
    }
}
