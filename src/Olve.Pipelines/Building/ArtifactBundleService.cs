using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sourcing;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Building;

public class ArtifactBundleService
{
    private readonly EntityStore<ArtifactBundle> _store;
    private readonly EntityStoreIndex<ArtifactBundle, Id<Pipeline>> _byPipeline;

    public ArtifactBundleService(EntityStore<ArtifactBundle> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(b => b.PipelineId);
    }

    public ArtifactBundle Create(Id<Pipeline> pipelineId, Id<SourceBundle> sourceBundleId)
    {
        var bundle = new ArtifactBundle(
            Id.New<ArtifactBundle>(),
            pipelineId,
            sourceBundleId,
            DateTimeOffset.UtcNow,
            ArtifactBundleStatus.Completed);

        _store.Set(bundle);
        return bundle;
    }

    public bool TryGet(Id<ArtifactBundle> id, [NotNullWhen(true)] out ArtifactBundle? bundle)
        => _store.TryGet(id, out bundle);

    public ArtifactBundle? GetLatest(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        ArtifactBundle? latest = null;
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var bundle) &&
                (latest is null || bundle.CreatedAt > latest.CreatedAt))
            {
                latest = bundle;
            }
        }
        return latest;
    }

    public IReadOnlyList<ArtifactBundle> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<ArtifactBundle>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var bundle))
                results.Add(bundle);
        }
        return results;
    }
}
