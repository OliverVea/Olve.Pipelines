using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes.Api;

public static class SecretEndpoints
{
    private static string SecretName(Id<Pipeline> pipelineId) => $"olve-pipeline-{pipelineId.Value.Value:N}";

    public static void MapSecretEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/secrets");

        group.MapGet("/", async Task<Result<string[]>> (
            PipelineService pipelines,
            KubernetesClient kubernetesClient,
            KubernetesOptions options,
            Id<Pipeline> pipelineId,
            CancellationToken ct) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
                return Result.Failure<string[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var data = await kubernetesClient.GetSecretAsync(options.Namespace, SecretName(pipelineId), ct);
            var names = data?.Keys.ToArray() ?? [];
            return Result.Success(names);
        })
        .WithResultMapping<string[]>()
        .WithName("ListSecrets");

        group.MapPut("/{name}", async Task<Result> (
            PipelineService pipelines,
            KubernetesClient kubernetesClient,
            KubernetesOptions options,
            Id<Pipeline> pipelineId,
            string name,
            SetSecretRequest request,
            CancellationToken ct) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
                return Result.Failure(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var secretName = SecretName(pipelineId);
            var existing = await kubernetesClient.GetSecretAsync(options.Namespace, secretName, ct);

            if (existing is null)
            {
                await kubernetesClient.CreateSecretAsync(options.Namespace, secretName,
                    new Dictionary<string, string> { [name] = request.Value }, ct);
            }
            else
            {
                existing[name] = request.Value;
                await kubernetesClient.UpdateSecretAsync(options.Namespace, secretName, existing, ct);
            }

            return Result.Success();
        })
        .WithResultMapping()
        .WithName("SetSecret");

        group.MapDelete("/{name}", async Task<Result> (
            PipelineService pipelines,
            KubernetesClient kubernetesClient,
            KubernetesOptions options,
            Id<Pipeline> pipelineId,
            string name,
            CancellationToken ct) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
                return Result.Failure(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var secretName = SecretName(pipelineId);
            var existing = await kubernetesClient.GetSecretAsync(options.Namespace, secretName, ct);

            if (existing is not null && existing.Remove(name))
            {
                if (existing.Count == 0)
                    await kubernetesClient.DeleteSecretAsync(options.Namespace, secretName, ct);
                else
                    await kubernetesClient.UpdateSecretAsync(options.Namespace, secretName, existing, ct);
            }

            return Result.Success();
        })
        .WithResultMapping()
        .WithName("DeleteSecret");

        // Server-side copy from another pipeline on this instance; values are never returned or logged.
        group.MapPost("/copy", Task<Result<CopySecretsResponse>> (
            PipelineSecretCopyService copies,
            HttpContext http,
            Id<Pipeline> pipelineId,
            CopySecretsRequest request,
            CancellationToken ct) => copies.CopyAsync(pipelineId, request, Actor(http.User), ct))
        .WithResultMapping<CopySecretsResponse>()
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
