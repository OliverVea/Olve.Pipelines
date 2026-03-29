using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineBuilds;

public class PipelineBuildService
{
    private readonly EntityStore<PipelineBuild> _store;
    private readonly EntityStoreIndex<PipelineBuild, Id<Pipeline>> _byPipeline;

    public PipelineBuildService(EntityStore<PipelineBuild> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(b => b.PipelineId);
    }

    public void Set(PipelineBuild build) => _store.Set(build);

    public bool TryGet(Id<PipelineBuild> id, [NotNullWhen(true)] out PipelineBuild? build)
        => _store.TryGet(id, out build);

    public IReadOnlyList<PipelineBuild> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<PipelineBuild>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var build))
                results.Add(build);
        }
        return results;
    }

    public bool Delete(Id<PipelineBuild> id) => _store.Delete(id);
}
