using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineBuilders;

public class PipelineBuilderService
{
    private readonly EntityStore<PipelineBuilder> _store;
    private readonly EntityStoreIndex<PipelineBuilder, Id<Pipeline>> _byPipeline;

    public PipelineBuilderService(EntityStore<PipelineBuilder> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(b => b.PipelineId);
    }

    public void Set(PipelineBuilder builder) => _store.Set(builder);

    public bool TryGet(Id<PipelineBuilder> id, [NotNullWhen(true)] out PipelineBuilder? builder)
        => _store.TryGet(id, out builder);

    public IReadOnlyList<PipelineBuilder> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<PipelineBuilder>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var builder))
                results.Add(builder);
        }
        return results;
    }

    public bool Delete(Id<PipelineBuilder> id) => _store.Delete(id);
}
