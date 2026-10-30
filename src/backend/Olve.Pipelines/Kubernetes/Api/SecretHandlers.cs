using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Shared.Problems;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Kubernetes.Api;

[OperationHandler]
public sealed class ListSecretsHandler(PipelineSecretService secrets) : Contract.ISecretsListHandler
{
    public async Task<Contract.SecretsListResponse> HandleAsync(Contract.SecretsListRequest request, CancellationToken cancellationToken)
    {
        var result = await secrets.ListAsync(PipelineViews.PipelineId(request.PipelineId), cancellationToken);
        if (result.TryPickProblems(out var problems, out var list))
            return new Contract.SecretsListResponse.NotFound(ApiProblems.Of([.. problems]));
        return new Contract.SecretsListResponse.Ok([.. list.Select(SecretViews.ToApi)]);
    }
}

/// <summary>Setting or deleting a secret re-runs the activation gate: a pending version waiting for it activates.</summary>
[OperationHandler]
public sealed class SetSecretHandler(PipelineSecretService secrets, ConfigActivationService gate, IServiceProvider services) : Contract.ISecretsSetHandler
{
    public async Task<Contract.SecretsSetResponse> HandleAsync(Contract.SecretsSetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var result = await secrets.SetAsync(id, request.Name, request.Body.Value, cancellationToken);
        if (!result.TryPickProblems(out var problems))
            result = SecretViews.Recheck(gate, services, id, await secrets.ListSetAsync(id, cancellationToken));
        if (result.TryPickProblems(out problems))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.SecretsSetResponse.NotFound(problem),
                StatusCodes.Status503ServiceUnavailable => new Contract.SecretsSetResponse.ServiceUnavailable(problem),
                _ => new Contract.SecretsSetResponse.BadRequest(problem),
            };
        }

        return new Contract.SecretsSetResponse.Ok();
    }
}

/// <summary>A secret the current config references is only deleted with force; new runs then wait until it is set again.</summary>
[OperationHandler]
public sealed class DeleteSecretHandler(PipelineSecretService secrets, ConfigActivationService gate, IServiceProvider services) : Contract.ISecretsDeleteHandler
{
    public async Task<Contract.SecretsDeleteResponse> HandleAsync(Contract.SecretsDeleteRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var before = await secrets.ListSetAsync(id, cancellationToken);
        if (request.Force != true && gate.ReferencingVersion(id, request.Name, before) is { } version)
            return new Contract.SecretsDeleteResponse.Conflict(ApiProblems.Of(new ConflictProblem(
                "The config referenced by version {0} uses secret '{1}'; delete it with force to hold new runs until it is set again.", version, request.Name)));
        var result = await secrets.DeleteAsync(id, request.Name, cancellationToken);
        if (!result.TryPickProblems(out var problems))
            result = SecretViews.Recheck(gate, services, id, await secrets.ListSetAsync(id, cancellationToken));
        if (result.TryPickProblems(out problems))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.SecretsDeleteResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Contract.SecretsDeleteResponse.Conflict(problem),
                StatusCodes.Status503ServiceUnavailable => new Contract.SecretsDeleteResponse.ServiceUnavailable(problem),
                _ => new Contract.SecretsDeleteResponse.BadRequest(problem),
            };
        }

        return new Contract.SecretsDeleteResponse.Ok();
    }
}

/// <summary>Server-side copy from another pipeline on this instance; values are never returned or logged.</summary>
[OperationHandler]
public sealed class CopySecretsHandler(PipelineSecretCopyService copies, IHttpContextAccessor http) : Contract.ISecretsCopyHandler
{
    public async Task<Contract.SecretsCopyResponse> HandleAsync(Contract.SecretsCopyRequest request, CancellationToken cancellationToken)
    {
        var body = request.Body;
        var domain = new CopySecretsRequest(new Id<Pipeline>(new Id(body.SourcePipelineId)), body.Names?.ToArray(), body.TargetName, body.Overwrite ?? false);
        var result = await copies.CopyAsync(PipelineViews.PipelineId(request.PipelineId), domain, SecretViews.Actor(http.HttpContext?.User), cancellationToken);
        if (result.TryPickProblems(out var problems, out var copied))
        {
            var problem = ApiProblems.Of([.. problems]);
            return problem.Status switch
            {
                StatusCodes.Status404NotFound => new Contract.SecretsCopyResponse.NotFound(problem),
                StatusCodes.Status409Conflict => new Contract.SecretsCopyResponse.Conflict(problem),
                StatusCodes.Status503ServiceUnavailable => new Contract.SecretsCopyResponse.ServiceUnavailable(problem),
                _ => new Contract.SecretsCopyResponse.BadRequest(problem),
            };
        }

        return new Contract.CopiedSecrets { Copied = copied.Copied };
    }
}

public static class SecretViews
{
    public static Contract.PipelineSecret ToApi(PipelineSecretInfo s) => new()
    {
        Name = s.Name,
        Description = s.Description,
        DeclaredBy = s.DeclaredBy,
        IsSet = s.IsSet,
        Needed = s.Needed,
    };

    public static Result Recheck(ConfigActivationService gate, IServiceProvider services, Id<Pipeline> pipelineId, IReadOnlySet<string>? setSecrets)
    {
        if (gate.Recheck(pipelineId, setSecrets).TryPickProblems(out var problems, out var activated))
            return Result.Failure(problems);
        if (activated is not null)
            ConfigService.ReleaseHeldPush(services, pipelineId);
        return Result.Success();
    }

    /// <summary>
    /// Who is calling, for the audit log: the OIDC username, falling back to name / email / subject
    /// (a client-credentials token without the profile scope carries only the subject).
    /// </summary>
    public static string Actor(System.Security.Claims.ClaimsPrincipal? user) =>
        user?.FindFirst("preferred_username")?.Value
        ?? user?.Identity?.Name
        ?? user?.FindFirst("email")?.Value
        ?? user?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? user?.FindFirst("sub")?.Value
        ?? "unknown";
}
