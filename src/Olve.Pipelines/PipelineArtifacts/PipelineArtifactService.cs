using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineArtifacts;

public class PipelineArtifactService
{
    private readonly EntityStore<PipelineArtifact> _store;
    private readonly EntityStoreIndex<PipelineArtifact, Id<PipelineBuilder>> _byBuilder;

    public PipelineArtifactService(EntityStore<PipelineArtifact> store)
    {
        _store = store;
        _byBuilder = store.CreateIndex(a => a.BuilderId);
    }

    public void Set(PipelineArtifact artifact) => _store.Set(artifact);

    public bool TryGet(Id<PipelineArtifact> id, [NotNullWhen(true)] out PipelineArtifact? artifact)
        => _store.TryGet(id, out artifact);

    public IReadOnlyList<PipelineArtifact> GetByBuilderId(Id<PipelineBuilder> builderId)
    {
        var ids = _byBuilder.GetForKey(builderId);
        var results = new List<PipelineArtifact>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var artifact))
                results.Add(artifact);
        }
        return results;
    }

    public bool Delete(Id<PipelineArtifact> id) => _store.Delete(id);
}
