using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingService(
    EntityStore<PipelineConfigBinding> store,
    IdProvider idProvider)
{
    private readonly EntityStoreIndex<PipelineConfigBinding, Id<Pipeline>> _byPipeline =
        store.CreateIndex(b => b.PipelineId);

    public Result<PipelineConfigBinding> Create(
        Id<Pipeline> pipelineId,
        string repo,
        string branch,
        string path,
        string? credentialsSecret)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return new ResultProblem("Binding requires a repository ('owner/name').");
        if (string.IsNullOrWhiteSpace(branch))
            return new ResultProblem("Binding requires a branch.");
        if (string.IsNullOrWhiteSpace(path))
            return new ResultProblem("Binding requires a config path (e.g. '.pipelines').");

        if (GetByPipelineId(pipelineId).Succeeded)
            return new ResultProblem($"Pipeline '{pipelineId}' is already bound to a repository.");

        var binding = new PipelineConfigBinding(
            idProvider.Create<PipelineConfigBinding>(),
            pipelineId,
            repo,
            branch,
            path,
            credentialsSecret,
            LastDeployedSha: null,
            DateTimeOffset.UtcNow);

        store.Set(binding);
        return binding;
    }

    /// <summary>
    /// Advances the deploy cursor after a production build has been enqueued for
    /// <paramref name="sha"/>. Idempotent: a no-op if the cursor already matches.
    /// </summary>
    public Result<PipelineConfigBinding> SetLastDeployedSha(Id<PipelineConfigBinding> id, string sha)
    {
        if (!store.TryGet(id, out var binding))
            return new ResultProblem($"Binding '{id}' not found.");

        if (binding.LastDeployedSha == sha)
            return binding;

        var updated = binding with { LastDeployedSha = sha };
        store.Set(updated);
        return updated;
    }

    public Result<PipelineConfigBinding> TryGet(Id<PipelineConfigBinding> id)
        => store.TryGet(id, out var binding)
            ? binding
            : Result.Failure<PipelineConfigBinding>(new ResultProblem($"Binding '{id}' not found."));

    public Result<PipelineConfigBinding> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var binding))
                return binding;
        }

        return Result.Failure<PipelineConfigBinding>(new ResultProblem($"Pipeline '{pipelineId}' has no config binding."));
    }

    public DeletionResult Delete(Id<PipelineConfigBinding> id) => store.Delete(id);
}
