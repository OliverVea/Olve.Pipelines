namespace Olve.Pipelines.Pipelines;

public interface IPipelineSecretStore
{
    Task<bool> HasSecretsAsync(Id<Pipeline> pipelineId, CancellationToken ct = default);
}
