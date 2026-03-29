using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.PipelineBuilds;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineArtifacts;

public class PipelineArtifactService
{
    private readonly EntityStore<PipelineArtifact> _store;
    private readonly EntityStoreIndex<PipelineArtifact, Id<PipelineBuild>> _byBuild;

    public PipelineArtifactService(EntityStore<PipelineArtifact> store)
    {
        _store = store;
        _byBuild = store.CreateIndex(a => a.BuildId);
    }

    public void Set(PipelineArtifact artifact) => _store.Set(artifact);

    public bool TryGet(Id<PipelineArtifact> id, [NotNullWhen(true)] out PipelineArtifact? artifact)
        => _store.TryGet(id, out artifact);

    public IReadOnlyList<PipelineArtifact> GetByBuildId(Id<PipelineBuild> buildId)
    {
        var ids = _byBuild.GetForKey(buildId);
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
