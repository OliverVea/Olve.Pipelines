using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;

namespace Olve.Pipelines.Kubernetes.Api;

public static class SecretEndpoints
{
    public static void MapSecretEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/secrets");

        // Declared (active version + binding) and set secrets; values are never returned.
        group.MapGet("/", Task<Result<PipelineSecretInfo[]>> (PipelineSecretService secrets, Id<Pipeline> pipelineId, CancellationToken ct)
                => secrets.ListAsync(pipelineId, ct))
            .WithApiResultMapping<PipelineSecretInfo[]>()
            .WithName("ListSecrets")
            .WithTags("Secrets")
            .WithSummary("List secrets")
            .WithDescription("The secrets the active version (steps, failure handlers, poll triggers) or the binding uses, plus set but unused ones, each with `declaredBy` and `isSet` (null when Kubernetes can't be read). Values are never returned.");

        // Setting or deleting a secret re-runs the activation gate: a pending version waiting for it activates.
        group.MapPut("/{name}", async Task<Result> (PipelineSecretService secrets, ConfigActivationService gate, HttpContext http, Id<Pipeline> pipelineId, string name, SetSecretRequest request, CancellationToken ct) =>
            {
                if ((await secrets.SetAsync(pipelineId, name, request.Value, ct)).TryPickProblems(out var problems))
                    return Result.Failure(problems);
                return Recheck(gate, http.RequestServices, pipelineId, await secrets.ListSetAsync(pipelineId, ct));
            })
            .WithApiResultMapping()
            .WithName("SetSecret")
            .WithTags("Secrets")
            .WithSummary("Set a secret")
            .WithDescription("Sets the value. Re-checks a pending version.");

        // A secret the current config references is only deleted with force=true; new runs then wait until it is set again.
        group.MapDelete("/{name}", async Task<Result> (PipelineSecretService secrets, ConfigActivationService gate, HttpContext http, Id<Pipeline> pipelineId, string name, bool? force, CancellationToken ct) =>
            {
                var before = await secrets.ListSetAsync(pipelineId, ct);
                if (force != true && gate.ReferencingVersion(pipelineId, name, before) is { } version)
                    return new ConflictProblem("The config referenced by version {0} uses secret '{1}'; delete it with force to hold new runs until it is set again.", version, name);
                if ((await secrets.DeleteAsync(pipelineId, name, ct)).TryPickProblems(out var problems))
                    return Result.Failure(problems);
                return Recheck(gate, http.RequestServices, pipelineId, await secrets.ListSetAsync(pipelineId, ct));
            })
            .WithApiResultMapping()
            .WithName("DeleteSecret")
            .WithTags("Secrets")
            .WithSummary("Delete a secret")
            .WithDescription("Deleting a secret the current config references needs `force=true` (409 without). Re-checks a pending version.");

        // Server-side copy from another pipeline on this instance; values are never returned or logged.
        group.MapPost("/copy", Task<Result<CopySecretsResponse>> (
            PipelineSecretCopyService copies,
            HttpContext http,
            Id<Pipeline> pipelineId,
            CopySecretsRequest request,
            CancellationToken ct) => copies.CopyAsync(pipelineId, request, Actor(http.User), ct))
        .WithApiResultMapping<CopySecretsResponse>()
        .WithName("CopySecrets")
        .WithTags("Secrets")
        .WithSummary("Copy secrets from another pipeline")
        .WithDescription("Copies values server-side from `sourcePipelineId` (`names`, default all; `targetName` renames a single one). Existing names are refused without `overwrite`. Answers the copied names. Whoever can write the target pipeline can read the copied values from its steps.");
    }

    private static Result Recheck(ConfigActivationService gate, IServiceProvider services, Id<Pipeline> pipelineId, IReadOnlySet<string>? setSecrets)
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
    internal static string Actor(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.Identity?.Name
        ?? user.FindFirst("email")?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? "unknown";
}

public record SetSecretRequest(string Value);
