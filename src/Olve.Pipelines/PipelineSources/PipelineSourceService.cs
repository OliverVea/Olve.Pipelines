using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.PipelineSources;

public class PipelineSourceService(
    EntityStore<PipelineSource> store,
    AttachmentStore<PipelineSource, HardcodedSource> hardcoded,
    AttachmentStore<PipelineSource, GitHubSource> github,
    IdProvider idProvider)
{
    private readonly EntityStoreIndex<PipelineSource, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);

    public Result<PipelineSource> Create(Id<Pipeline> pipelineId, string name)
    {
        var source = new PipelineSource(idProvider.Create<PipelineSource>(), name, pipelineId);
        store.Set(source);
        return source;
    }

    public Result<PipelineSource> TryGet(Id<PipelineSource> id)
        => store.TryGet(id, out var source)
            ? source
            : Result.Failure<PipelineSource>(new ResultProblem($"Source '{id}' not found."));

    public Result<PipelineSource[]> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<PipelineSource>(ids.Count);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var source))
                results.Add(source);
        }
        return results.ToArray();
    }

    public DeletionResult Delete(Id<PipelineSource> id) => store.Delete(id);

    public Result<HardcodedSource> SetHardcoded(Id<PipelineSource> id, HardcodedSource source)
    {
        if (!store.TryGet(id, out _))
            return Result.Failure<HardcodedSource>(new ResultProblem($"Source '{id}' not found."));

        github.Remove(id);
        hardcoded.Set(id, source);
        UpdateType(id, PipelineSourceType.Hardcoded);
        return source;
    }

    public Result<HardcodedSource> TryGetHardcoded(Id<PipelineSource> id)
        => hardcoded.TryGet(id, out var source)
            ? source
            : Result.Failure<HardcodedSource>(new ResultProblem($"Source '{id}' has no hardcoded configuration."));

    public Result RemoveHardcoded(Id<PipelineSource> id)
    {
        if (!hardcoded.Remove(id))
            return Result.Failure(new ResultProblem($"Source '{id}' has no hardcoded configuration."));

        UpdateType(id, PipelineSourceType.None);
        return Result.Success();
    }

    public Result<GitHubSource> SetGitHub(Id<PipelineSource> id, GitHubSource source)
    {
        if (!store.TryGet(id, out _))
            return Result.Failure<GitHubSource>(new ResultProblem($"Source '{id}' not found."));

        hardcoded.Remove(id);
        github.Set(id, source);
        UpdateType(id, PipelineSourceType.GitHub);
        return source;
    }

    public Result<GitHubSource> TryGetGitHub(Id<PipelineSource> id)
        => github.TryGet(id, out var source)
            ? source
            : Result.Failure<GitHubSource>(new ResultProblem($"Source '{id}' has no GitHub configuration."));

    public Result RemoveGitHub(Id<PipelineSource> id)
    {
        if (!github.Remove(id))
            return Result.Failure(new ResultProblem($"Source '{id}' has no GitHub configuration."));

        UpdateType(id, PipelineSourceType.None);
        return Result.Success();
    }

    private void UpdateType(Id<PipelineSource> id, PipelineSourceType type)
    {
        if (store.TryGet(id, out var entity))
            store.Set(entity with { Type = type });
    }
}
