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
    }
}

public record SetSecretRequest(string Value);
