using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineSources;

public class PipelineSourceService
{
    private readonly EntityStore<PipelineSource> _store;
    private readonly EntityStoreIndex<PipelineSource, Id<Pipeline>> _byPipeline;

    public PipelineSourceService(EntityStore<PipelineSource> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(s => s.PipelineId);
    }

    public void Set(PipelineSource source) => _store.Set(source);

    public bool TryGet(Id<PipelineSource> id, [NotNullWhen(true)] out PipelineSource? source)
        => _store.TryGet(id, out source);

    public IReadOnlyList<PipelineSource> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<PipelineSource>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var source))
                results.Add(source);
        }
        return results;
    }

    public bool Delete(Id<PipelineSource> id) => _store.Delete(id);
}
