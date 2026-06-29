using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public class ProcessingStepCleanupService(EntityStore<ProcessingStep> store)
{
    private readonly EntityStoreIndex<ProcessingStep, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);

    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
            _ = store.Delete(id); // cascade delete; NotFound (already gone) is benign
    }
}
