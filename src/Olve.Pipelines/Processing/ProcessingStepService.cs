using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Processing;

public class ProcessingStepService
{
    private readonly EntityStore<ProcessingStep> _store;
    private readonly EntityStoreIndex<ProcessingStep, Id<Pipeline>> _byPipeline;

    public ProcessingStepService(EntityStore<ProcessingStep> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(p => p.PipelineId);
    }

    public void Set(ProcessingStep step) => _store.Set(step);

    public bool TryGet(Id<ProcessingStep> id, [NotNullWhen(true)] out ProcessingStep? step)
        => _store.TryGet(id, out step);

    public IReadOnlyList<ProcessingStep> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<ProcessingStep>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var step))
                results.Add(step);
        }
        return results;
    }

    public bool Delete(Id<ProcessingStep> id) => _store.Delete(id);
}
