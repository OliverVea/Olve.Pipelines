using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.PipelineSources;

public class PipelineSourceService
{
    private readonly EntityStore<PipelineSource> _store;
    private readonly EntityStoreIndex<PipelineSource, Id<Pipeline>> _byPipeline;
    private readonly AttachmentStore<PipelineSource, HardcodedSource> _hardcoded;
    private readonly AttachmentStore<PipelineSource, GitHubSource> _github;

    public PipelineSourceService(
        EntityStore<PipelineSource> store,
        AttachmentStore<PipelineSource, HardcodedSource> hardcoded,
        AttachmentStore<PipelineSource, GitHubSource> github)
    {
        _store = store;
        _byPipeline = store.CreateIndex(s => s.PipelineId);
        _hardcoded = hardcoded;
        _github = github;
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

    public DeletionResult Delete(Id<PipelineSource> id) => _store.Delete(id);

    public void SetHardcoded(Id<PipelineSource> id, HardcodedSource source)
    {
        _github.Remove(id);
        _hardcoded.Set(id, source);
        UpdateType(id, PipelineSourceType.Hardcoded);
    }

    public bool TryGetHardcoded(Id<PipelineSource> id, [NotNullWhen(true)] out HardcodedSource? source)
        => _hardcoded.TryGet(id, out source);

    public bool RemoveHardcoded(Id<PipelineSource> id)
    {
        if (!_hardcoded.Remove(id)) return false;
        UpdateType(id, PipelineSourceType.None);
        return true;
    }

    public void SetGitHub(Id<PipelineSource> id, GitHubSource source)
    {
        _hardcoded.Remove(id);
        _github.Set(id, source);
        UpdateType(id, PipelineSourceType.GitHub);
    }

    public bool TryGetGitHub(Id<PipelineSource> id, [NotNullWhen(true)] out GitHubSource? source)
        => _github.TryGet(id, out source);

    public bool RemoveGitHub(Id<PipelineSource> id)
    {
        if (!_github.Remove(id)) return false;
        UpdateType(id, PipelineSourceType.None);
        return true;
    }

    private void UpdateType(Id<PipelineSource> id, PipelineSourceType type)
    {
        if (_store.TryGet(id, out var entity))
            _store.Set(entity with { Type = type });
    }
}
