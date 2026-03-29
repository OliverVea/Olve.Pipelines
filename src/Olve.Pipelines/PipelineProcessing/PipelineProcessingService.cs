using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineProcessing;

public class PipelineProcessingService
{
    private readonly EntityStore<PipelineProcessingStep> _store;
    private readonly EntityStoreIndex<PipelineProcessingStep, Id<Pipeline>> _byPipeline;

    public PipelineProcessingService(EntityStore<PipelineProcessingStep> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(p => p.PipelineId);
    }

    public void Set(PipelineProcessingStep processing) => _store.Set(processing);

    public bool TryGet(Id<PipelineProcessingStep> id, [NotNullWhen(true)] out PipelineProcessingStep? processing)
        => _store.TryGet(id, out processing);

    public IReadOnlyList<PipelineProcessingStep> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<PipelineProcessingStep>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var processing))
                results.Add(processing);
        }
        return results;
    }

    public bool Delete(Id<PipelineProcessingStep> id) => _store.Delete(id);
}
