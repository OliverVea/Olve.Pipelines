using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.Cli.Api;

/// <summary>Maps each <see cref="IPipelinesApi"/> operation to a route + verb over <see cref="ApiTransport"/>.</summary>
public sealed class PipelinesApi(ApiTransport transport) : IPipelinesApi
{
    private static string Enc(string segment) => Uri.EscapeDataString(segment);

    // Pipelines
    public Task<Result<Pipeline[]>> ListPipelines(CancellationToken ct) =>
        transport.GetAsync("/api/pipelines/", CliJsonContext.Default.PipelineArray, ct);

    public Task<Result<PipelineSummary[]>> ListPipelineSummaries(CancellationToken ct) =>
        transport.GetAsync("/api/pipelines/summary", CliJsonContext.Default.PipelineSummaryArray, ct);

    public Task<Result<Pipeline>> GetPipeline(string id, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(id)}", CliJsonContext.Default.Pipeline, ct);

    // Jobs
    public Task<Result<PageOfJob>> ListJobs(string? pipelineId, int page, int pageSize, int sort, CancellationToken ct)
    {
        var query = $"?Page={page}&PageSize={pageSize}&Sort={sort}";
        if (!string.IsNullOrEmpty(pipelineId))
            query += $"&PipelineId={Enc(pipelineId)}";
        return transport.GetAsync($"/api/jobs/{query}", CliJsonContext.Default.PageOfJob, ct);
    }

    public Task<Result<Job>> GetJob(string id, CancellationToken ct) =>
        transport.GetAsync($"/api/jobs/{Enc(id)}", CliJsonContext.Default.Job, ct);

    public Task<Result<string>> GetJobLogs(string id, CancellationToken ct) =>
        transport.GetStringAsync($"/api/jobs/{Enc(id)}/logs", ct);

    public Task<Result<string>> GetJobQueueRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/jobs/queue", ct);

    // Secrets
    public Task<Result<string[]>> ListSecrets(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/secrets", CliJsonContext.Default.StringArray, ct);

    // Bindings (GitOps)
    public Task<Result<PipelineConfigBinding>> GetPipelineBinding(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/binding", CliJsonContext.Default.PipelineConfigBinding, ct);

    public Task<Result<PipelineBindingStatus>> GetPipelineBindingStatus(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/binding/status", CliJsonContext.Default.PipelineBindingStatus, ct);

    // Info
    public Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/config/frontend", ct);

    public Task<Result<string>> GetHealthRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/health", ct);

    public Task<Result<string>> GetReadyRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/ready", ct);

    // Mutations
    public Task<Result> CancelJob(string id, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/jobs/{Enc(id)}/cancel", body: null, ct);

    public Task<Result> DeletePipeline(string id, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"/api/pipelines/{Enc(id)}", body: null, ct);

    public Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"/api/pipelines/{Enc(pipelineId)}/secrets/{Enc(name)}",
            ApiTransport.JsonBody(new SetSecretRequest { Value = value }, CliJsonContext.Default.SetSecretRequest), ct);

    public Task<Result> DeleteSecret(string pipelineId, string name, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"/api/pipelines/{Enc(pipelineId)}/secrets/{Enc(name)}", body: null, ct);

    public Task<Result<PipelineConfigBinding>> CreatePipelineWithRepo(
        string repo, string? branch, string? path, string? credentialsSecret,
        BindingDeployTrigger? deployTrigger, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, "/api/pipelines/with-repo",
            ApiTransport.JsonBody(
                new CreatePipelineWithRepoRequest
                {
                    Repo = repo, Branch = branch, Path = path,
                    CredentialsSecret = credentialsSecret, DeployTrigger = deployTrigger,
                },
                CliJsonContext.Default.CreatePipelineWithRepoRequest),
            CliJsonContext.Default.PipelineConfigBinding, ct);

    public Task<Result<PipelineConfigBinding>> UpdatePipelineBindingCredentials(
        string pipelineId, string? credentialsSecret, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Patch, $"/api/pipelines/{Enc(pipelineId)}/binding",
            ApiTransport.JsonBody(
                new UpdateBindingRequest { CredentialsSecret = credentialsSecret },
                CliJsonContext.Default.UpdateBindingRequest),
            CliJsonContext.Default.PipelineConfigBinding, ct);

    public Task<Result<PipelineConfigBinding>> UpdatePipelineBindingDeployTrigger(
        string pipelineId, BindingDeployTrigger deployTrigger, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Patch, $"/api/pipelines/{Enc(pipelineId)}/binding/deploy-trigger",
            ApiTransport.JsonBody(
                new SetDeployTriggerRequest { DeployTrigger = deployTrigger },
                CliJsonContext.Default.SetDeployTriggerRequest),
            CliJsonContext.Default.PipelineConfigBinding, ct);

    public Task<Result> ReconcilePipelineBinding(string pipelineId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/binding/reconcile", body: null, ct);
}
