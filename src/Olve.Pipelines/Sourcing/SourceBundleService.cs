using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Sourcing;

public class SourceBundleService
{
    private readonly EntityStore<SourceBundle> _store;
    private readonly EntityStoreIndex<SourceBundle, Id<Pipeline>> _byPipeline;

    public SourceBundleService(EntityStore<SourceBundle> store)
    {
        _store = store;
        _byPipeline = store.CreateIndex(b => b.PipelineId);
    }

    public SourceBundle Create(Id<Pipeline> pipelineId, SourceBundleStatus status = SourceBundleStatus.Completed)
    {
        var bundle = new SourceBundle(
            Id.New<SourceBundle>(),
            pipelineId,
            DateTimeOffset.UtcNow,
            status);

        _store.Set(bundle);
        return bundle;
    }

    public void UpdateStatus(Id<SourceBundle> id, SourceBundleStatus status)
    {
        if (_store.TryGet(id, out var bundle))
            _store.Set(bundle with { Status = status });
    }

    public bool TryGet(Id<SourceBundle> id, [NotNullWhen(true)] out SourceBundle? bundle)
        => _store.TryGet(id, out bundle);

    public SourceBundle? GetLatest(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        SourceBundle? latest = null;
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

    public IReadOnlyList<SourceBundle> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<SourceBundle>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var bundle))
                results.Add(bundle);
        }
        return results;
    }
}
