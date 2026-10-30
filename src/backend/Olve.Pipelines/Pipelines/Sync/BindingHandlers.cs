using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared.Problems;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>The only way to create a pipeline: bound to a repo. Rolls the pipeline back if binding fails, so a failed bind never leaves an orphan.</summary>
[OperationHandler]
public sealed class CreatePipelineHandler(PipelineService pipelines, PipelineConfigBindingService bindings) : Contract.IBindingsCreateHandler
{
    private const string DefaultBranch = "main";
    private const string DefaultPath = ".pipelines";

    public Task<Contract.BindingsCreateResponse> HandleAsync(Contract.BindingsCreateRequest request, CancellationToken cancellationToken)
    {
        var body = request.Body;
        if (pipelines.Create(ProvisionalName(body.Repo)).TryPickProblems(out var pipelineProblems, out var pipeline))
            return Task.FromResult(Fail(pipelineProblems));

        var bound = bindings.Create(pipeline.Id, body.Repo,
            string.IsNullOrWhiteSpace(body.Branch) ? DefaultBranch : body.Branch,
            string.IsNullOrWhiteSpace(body.Path) ? DefaultPath : body.Path,
            body.CredentialsSecret);
        if (bound.TryPickProblems(out var bindingProblems, out var binding))
        {
            pipelines.Delete(pipeline.Id).DiscardResult(); // NotFound is benign
            return Task.FromResult(Fail(bindingProblems));
        }

        return Task.FromResult<Contract.BindingsCreateResponse>(new Contract.BindingsCreateResponse.Created(BindingViews.ToApi(binding)));
    }

    private static Contract.BindingsCreateResponse Fail(ResultProblemCollection problems)
    {
        var problem = ApiProblems.Of([.. problems]);
        return problem.Status == StatusCodes.Status409Conflict
            ? new Contract.BindingsCreateResponse.Conflict(problem)
            : new Contract.BindingsCreateResponse.BadRequest(problem);
    }

    // Until the first reconcile sets the manifest's name: "OliverVea/Olve.Pipelines" -> "Olve.Pipelines".
    private static string ProvisionalName(string repo)
    {
        var trimmed = repo.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var segment = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(segment) ? repo : segment;
    }
}

[OperationHandler]
public sealed class GetBindingHandler(PipelineConfigBindingService bindings) : Contract.IBindingsGetHandler
{
    public Task<Contract.BindingsGetResponse> HandleAsync(Contract.BindingsGetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Contract.BindingsGetResponse>(bindings.GetByPipelineId(PipelineViews.PipelineId(request.PipelineId)).TryPickProblems(out var problems, out var binding)
            ? new Contract.BindingsGetResponse.NotFound(ApiProblems.Of([.. problems]))
            : BindingViews.ToApi(binding));
}

[OperationHandler]
public sealed class GetBindingStatusHandler(
    PipelineConfigBindingService bindings, PipelineActivationService activations, PipelineVersionService versions, ConfigActivationService gate)
    : Contract.IBindingsStatusHandler
{
    public Task<Contract.BindingsStatusResponse> HandleAsync(Contract.BindingsStatusRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (bindings.GetByPipelineId(id).TryPickProblems(out var problems, out var binding))
            return Task.FromResult<Contract.BindingsStatusResponse>(new Contract.BindingsStatusResponse.NotFound(ApiProblems.Of([.. problems])));

        var status = binding.Status;
        return Task.FromResult<Contract.BindingsStatusResponse>(new Contract.PipelineBindingStatus
        {
            PipelineId = binding.PipelineId.Value.Value,
            Repo = binding.Repo,
            Branch = binding.Branch,
            Path = binding.Path,
            LastPushSha = binding.LastPushSha,
            LastReconciledSha = binding.LastReconciledSha,
            Result = BindingViews.ToApi(status.Result),
            LastSyncTime = status.LastSyncTime,
            Problems = status.Problems,
            Warnings = status.Warnings ?? [],
            ReconcileSha = status.Sha,
            ReconcileInProgress = status.InProgress,
            // Snapshots from before VersionNumber existed: the version built from the reconciled commit.
            ReconciledVersion = status.VersionNumber ?? (status.Result == ReconcileResult.Success
                ? versions.ListByPipeline(id).FirstOrDefault(v => v.CommitSha == status.Sha)?.Number
                : null),
            Activation = PipelineViews.ToApi(activations.TryGet(id, out var activation) ? activation : null),
            Pending = gate.PendingInfo(id) is { } pending ? PipelineViews.ToApi(pending) : null,
        });
    }
}

/// <summary>The credentials key moves GitHub reads onto the authenticated rate limit; null clears it.</summary>
[OperationHandler]
public sealed class UpdateBindingHandler(PipelineConfigBindingService bindings) : Contract.IBindingsUpdateHandler
{
    public Task<Contract.BindingsUpdateResponse> HandleAsync(Contract.BindingsUpdateRequest request, CancellationToken cancellationToken)
    {
        if (bindings.SetCredentialsSecret(PipelineViews.PipelineId(request.PipelineId), request.Body.CredentialsSecret).TryPickProblems(out var problems, out var binding))
        {
            var problem = ApiProblems.Of([.. problems]);
            return Task.FromResult<Contract.BindingsUpdateResponse>(problem.Status == StatusCodes.Status404NotFound
                ? new Contract.BindingsUpdateResponse.NotFound(problem)
                : new Contract.BindingsUpdateResponse.BadRequest(problem));
        }

        return Task.FromResult<Contract.BindingsUpdateResponse>(BindingViews.ToApi(binding));
    }
}

[OperationHandler]
public sealed class SetBindingBranchHandler(PipelineConfigBindingService bindings) : Contract.IBindingsSetBranchHandler
{
    public Task<Contract.BindingsSetBranchResponse> HandleAsync(Contract.BindingsSetBranchRequest request, CancellationToken cancellationToken)
    {
        if (bindings.SetBranch(PipelineViews.PipelineId(request.PipelineId), request.Body.Branch).TryPickProblems(out var problems, out var binding))
        {
            var problem = ApiProblems.Of([.. problems]);
            return Task.FromResult<Contract.BindingsSetBranchResponse>(problem.Status == StatusCodes.Status404NotFound
                ? new Contract.BindingsSetBranchResponse.NotFound(problem)
                : new Contract.BindingsSetBranchResponse.BadRequest(problem));
        }

        return Task.FromResult<Contract.BindingsSetBranchResponse>(BindingViews.ToApi(binding));
    }
}

[OperationHandler]
public sealed class SetBindingPollingHandler(PipelineConfigBindingService bindings) : Contract.IBindingsSetPollingHandler
{
    public Task<Contract.BindingsSetPollingResponse> HandleAsync(Contract.BindingsSetPollingRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Contract.BindingsSetPollingResponse>(bindings.SetPolls(PipelineViews.PipelineId(request.PipelineId), request.Body.Polls).TryPickProblems(out var problems, out var binding)
            ? new Contract.BindingsSetPollingResponse.NotFound(ApiProblems.Of([.. problems]))
            : BindingViews.ToApi(binding));
}

/// <summary>Returns at once; the reconcile runs in the background. With wait, returns once it finished. Also retries a commit whose reconcile failed, which the poll loop never does.</summary>
[OperationHandler]
public sealed class ReconcileBindingHandler(DeployPollService deployPoll, PipelineConfigBindingService bindings) : Contract.IBindingsReconcileHandler
{
    public async Task<Contract.BindingsReconcileResponse> HandleAsync(Contract.BindingsReconcileRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var rebuild = request.Rebuild == true;
        Result result;
        if (request.Wait != true)
            result = deployPoll.RequestReconcile(id, force: true, rebuild: rebuild);
        else if ((await deployPoll.ReconcileNowAsync(id, cancellationToken, rebuild: rebuild)).TryPickProblems(out var problems))
            result = Result.Failure(problems);
        else if (bindings.GetByPipelineId(id).TryPickProblems(out var bindingProblems, out var binding))
            result = Result.Failure(bindingProblems);
        else
            result = binding.Status.Result == ReconcileResult.Error
                ? new ConflictProblem("Reconcile of {0} failed: {1}", binding.Status.Sha ?? "the bound config", string.Join("; ", binding.Status.Problems))
                : Result.Success();

        if (result.TryPickProblems(out var failed))
        {
            var problem = ApiProblems.Of([.. failed]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.BindingsReconcileResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Contract.BindingsReconcileResponse.Conflict(problem),
                _ => new Contract.BindingsReconcileResponse.BadRequest(problem),
            };
        }

        return new Contract.BindingsReconcileResponse.Ok();
    }
}

public static class BindingViews
{
    /// <summary>The webhook HMAC key is not a property of the contract's binding: it never leaves the service.</summary>
    public static Contract.PipelineBinding ToApi(PipelineConfigBinding b) => new()
    {
        Id = b.Id.Value.Value,
        PipelineId = b.PipelineId.Value.Value,
        Repo = b.Repo,
        Branch = b.Branch,
        Path = b.Path,
        CredentialsSecret = b.CredentialsSecret,
        LastPushSha = b.LastPushSha,
        LastReconciledSha = b.LastReconciledSha,
        Status = new Contract.ReconcileStatus
        {
            Result = ToApi(b.Status.Result),
            LastSyncTime = b.Status.LastSyncTime,
            Problems = b.Status.Problems,
            Warnings = b.Status.Warnings ?? [],
            Sha = b.Status.Sha,
            InProgress = b.Status.InProgress,
            VersionNumber = b.Status.VersionNumber,
        },
        CreatedAt = b.CreatedAt,
        Polls = b.Polls,
    };

    public static Contract.ReconcileResult ToApi(ReconcileResult r) => r switch
    {
        ReconcileResult.Success => Contract.ReconcileResult.Success,
        ReconcileResult.Error => Contract.ReconcileResult.Error,
        _ => Contract.ReconcileResult.NeverRun,
    };
}
