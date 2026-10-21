using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Typed facade over the Olve.Pipelines HTTP API used by CLI commands. Each method maps one
/// operation to a path + verb and returns a <see cref="Result{T}"/>. Hand-rolled (not Refit) so
/// the <c>pl</c> binary stays Native-AOT. Grown incrementally as commands are added.
/// </summary>
public interface IPipelinesApi
{
    // Pipelines
    Task<Result<Pipeline[]>> ListPipelines(CancellationToken ct);
    Task<Result<PipelineSummary[]>> ListPipelineSummaries(CancellationToken ct);
    Task<Result<Pipeline>> GetPipeline(string id, CancellationToken ct);

    // Jobs
    Task<Result<PageOfJob>> ListJobs(string? pipelineId, int page, int pageSize, int sort, CancellationToken ct);
    Task<Result<Job>> GetJob(string id, CancellationToken ct);
    Task<Result<string>> GetJobLogs(string id, CancellationToken ct);
    Task<Result<string>> GetJobQueueRaw(CancellationToken ct);

    // Secrets
    Task<Result<string[]>> ListSecrets(string pipelineId, CancellationToken ct);

    // Bindings (GitOps)
    Task<Result<PipelineConfigBinding>> GetPipelineBinding(string pipelineId, CancellationToken ct);
    Task<Result<PipelineBindingStatus>> GetPipelineBindingStatus(string pipelineId, CancellationToken ct);

    // Info
    Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct);
    Task<Result<string>> GetHealthRaw(CancellationToken ct);
    Task<Result<string>> GetReadyRaw(CancellationToken ct);

    // Mutations
    Task<Result> CancelJob(string id, CancellationToken ct);
    Task<Result> DeletePipeline(string id, CancellationToken ct);
    Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct);
    Task<Result> DeleteSecret(string pipelineId, string name, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> CreatePipelineWithRepo(
        string repo, string? branch, string? path, string? credentialsSecret,
        BindingDeployTrigger? deployTrigger, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> UpdatePipelineBindingCredentials(
        string pipelineId, string? credentialsSecret, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> UpdatePipelineBindingDeployTrigger(
        string pipelineId, BindingDeployTrigger deployTrigger, CancellationToken ct);
    Task<Result> ReconcilePipelineBinding(string pipelineId, CancellationToken ct);
}
