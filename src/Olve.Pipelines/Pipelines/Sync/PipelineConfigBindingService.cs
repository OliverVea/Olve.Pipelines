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
        string? credentialsSecret,
        BindingDeployTrigger deployTrigger = BindingDeployTrigger.Webhook)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return ValidationProblem.ForField("repo", "Binding requires a repository ('owner/name').");
        if (string.IsNullOrWhiteSpace(branch))
            return ValidationProblem.ForField("branch", "Binding requires a branch.");
        if (string.IsNullOrWhiteSpace(path))
            return ValidationProblem.ForField("path", "Binding requires a config path (e.g. '.pipelines').");

        if (GetByPipelineId(pipelineId).Succeeded)
            return new ConflictProblem("Pipeline '{0}' is already bound to a repository.", pipelineId);

        var binding = new PipelineConfigBinding(
            idProvider.Create<PipelineConfigBinding>(),
            pipelineId,
            repo,
            branch,
            path,
            credentialsSecret,
            LastPushSha: null,
            LastReconciledSha: null,
            ReconcileStatus.NeverRun,
            DateTimeOffset.UtcNow,
            deployTrigger,
            // A webhook mode needs an HMAC key from the start; poll mode never uses one.
            WebhookSecret: deployTrigger == BindingDeployTrigger.Poll ? null : idProvider.CreateSecret());

        store.Set(binding);
        return binding;
    }

    /// <summary>
    /// Changes how a bound pipeline is triggered (webhook / webhook-only / poll), settable any time
    /// after binding. Generates the HMAC webhook secret on the first switch into a webhook mode so an
    /// existing poll-bound pipeline can adopt webhooks without re-binding.
    /// </summary>
    public Result<PipelineConfigBinding> SetDeployTrigger(Id<Pipeline> pipelineId, BindingDeployTrigger deployTrigger)
    {
        if (GetByPipelineId(pipelineId).TryPickProblems(out var problems, out var binding))
            return problems;

        var secret = binding.WebhookSecret;
        if (deployTrigger != BindingDeployTrigger.Poll && string.IsNullOrEmpty(secret))
            secret = idProvider.CreateSecret();

        var updated = binding with { DeployTrigger = deployTrigger, WebhookSecret = secret };
        store.Set(updated);
        return updated;
    }

    /// <summary>
    /// Records the branch head the push trigger fired for. Idempotent.
    /// </summary>
    public Result<PipelineConfigBinding> SetLastPushSha(Id<PipelineConfigBinding> id, string sha)
    {
        if (!store.TryGet(id, out var binding))
            return new NotFoundProblem("Binding '{0}' not found.", id);

        if (binding.LastPushSha == sha)
            return binding;

        var updated = binding with { LastPushSha = sha };
        store.Set(updated);
        return updated;
    }

    /// <summary>
    /// Advances the config cursor after a fully successful reconcile applied <paramref name="sha"/>.
    /// Idempotent: a no-op if the cursor already matches.
    /// </summary>
    public Result<PipelineConfigBinding> SetLastReconciledSha(Id<PipelineConfigBinding> id, string sha)
    {
        if (!store.TryGet(id, out var binding))
            return new NotFoundProblem("Binding '{0}' not found.", id);

        if (binding.LastReconciledSha == sha)
            return binding;

        var updated = binding with { LastReconciledSha = sha };
        store.Set(updated);
        return updated;
    }

    /// <summary>
    /// Sets (or clears, with null/empty) the k8s secret key the config fetch authenticates with.
    /// Lets an operator authenticate an existing binding without re-binding — e.g. to move its
    /// GitHub fetches off the unauthenticated 60/hr rate limit onto the 5000/hr token bucket.
    /// </summary>
    public Result<PipelineConfigBinding> SetCredentialsSecret(Id<Pipeline> pipelineId, string? credentialsSecret)
    {
        if (GetByPipelineId(pipelineId).TryPickProblems(out var problems, out var binding))
            return problems;

        var normalized = string.IsNullOrWhiteSpace(credentialsSecret) ? null : credentialsSecret;
        var updated = binding with { CredentialsSecret = normalized };
        store.Set(updated);
        return updated;
    }

    /// <summary>Records the outcome of the most recent reconcile attempt.</summary>
    public Result<PipelineConfigBinding> SetReconcileStatus(Id<PipelineConfigBinding> id, ReconcileStatus status)
    {
        if (!store.TryGet(id, out var binding))
            return new NotFoundProblem("Binding '{0}' not found.", id);

        var updated = binding with { Status = status };
        store.Set(updated);
        return updated;
    }

    public Result<PipelineConfigBinding> TryGet(Id<PipelineConfigBinding> id)
        => store.TryGet(id, out var binding)
            ? binding
            : Result.Failure<PipelineConfigBinding>(new NotFoundProblem("Binding '{0}' not found.", id));

    public Result<PipelineConfigBinding> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var binding))
                return binding;
        }

        return Result.Failure<PipelineConfigBinding>(new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId));
    }

    public DeletionResult Delete(Id<PipelineConfigBinding> id) => store.Delete(id);
}
