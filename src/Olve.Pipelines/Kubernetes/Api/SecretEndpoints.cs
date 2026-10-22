using Olve.Pipelines.Pipelines;

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
            .WithName("ListSecrets");

        group.MapPut("/{name}", Task<Result> (PipelineSecretService secrets, Id<Pipeline> pipelineId, string name, SetSecretRequest request, CancellationToken ct)
                => secrets.SetAsync(pipelineId, name, request.Value, ct))
            .WithApiResultMapping()
            .WithName("SetSecret");

        group.MapDelete("/{name}", Task<Result> (PipelineSecretService secrets, Id<Pipeline> pipelineId, string name, CancellationToken ct)
                => secrets.DeleteAsync(pipelineId, name, ct))
            .WithApiResultMapping()
            .WithName("DeleteSecret");

        // Server-side copy from another pipeline on this instance; values are never returned or logged.
        group.MapPost("/copy", Task<Result<CopySecretsResponse>> (
            PipelineSecretCopyService copies,
            HttpContext http,
            Id<Pipeline> pipelineId,
            CopySecretsRequest request,
            CancellationToken ct) => copies.CopyAsync(pipelineId, request, Actor(http.User), ct))
        .WithApiResultMapping<CopySecretsResponse>()
        .WithName("CopySecrets");
    }

    /// <summary>
    /// Who is calling, for the audit log: the OIDC username, falling back to name / email / subject
    /// (a client-credentials token without the profile scope carries only the subject).
    /// </summary>
    private static string Actor(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.Identity?.Name
        ?? user.FindFirst("email")?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? "unknown";
}

public record SetSecretRequest(string Value);
