using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Building;

public class ArtifactBundleService
{
    private readonly EntityStore<ArtifactBundle> _store;
    private readonly EntityStoreIndex<ArtifactBundle, Id<Pipeline>> _byPipeline;

    public ArtifactBundleService(EntityStore<ArtifactBundle> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(b => b.PipelineId);
    }

    public ArtifactBundle Create(Id<Pipeline> pipelineId, ArtifactBundleStatus status = ArtifactBundleStatus.Completed)
    {
        var bundle = new ArtifactBundle(
            Id.New<ArtifactBundle>(),
            pipelineId,
            DateTimeOffset.UtcNow,
            status);

        _store.Set(bundle);
        return bundle;
    }

    public Result UpdateStatus(Id<ArtifactBundle> id, ArtifactBundleStatus status)
        => _store.Mutate(id, b => b with { Status = status });

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
