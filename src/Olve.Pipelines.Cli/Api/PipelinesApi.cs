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

    public Task<Result<string>> GetPipelineDocumentRaw(string id, CancellationToken ct) =>
        transport.GetStringAsync($"/api/pipelines/{Enc(id)}/document", ct);

    // Production steps
    public Task<Result<ProductionStep[]>> ListProductionSteps(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/production/", CliJsonContext.Default.ProductionStepArray, ct);

    public Task<Result<ProductionStep>> GetProductionStep(string stepId, CancellationToken ct) =>
        transport.GetAsync($"/api/production-steps/{Enc(stepId)}/", CliJsonContext.Default.ProductionStep, ct);

    public Task<Result<StepConfiguration>> GetProductionStepConfiguration(string stepId, CancellationToken ct) =>
        transport.GetAsync($"/api/production-steps/{Enc(stepId)}/configuration", CliJsonContext.Default.StepConfiguration, ct);

    // Processing steps
    public Task<Result<ProcessingStep[]>> ListProcessingSteps(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/processing/", CliJsonContext.Default.ProcessingStepArray, ct);

    public Task<Result<ProcessingStep>> GetProcessingStep(string stepId, CancellationToken ct) =>
        transport.GetAsync($"/api/processing-steps/{Enc(stepId)}/", CliJsonContext.Default.ProcessingStep, ct);

    public Task<Result<StepConfiguration>> GetProcessingStepConfiguration(string stepId, CancellationToken ct) =>
        transport.GetAsync($"/api/processing-steps/{Enc(stepId)}/configuration", CliJsonContext.Default.StepConfiguration, ct);

    public Task<Result<StepPromotionState[]>> ListProcessingStepPromotions(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/processing/promotions", CliJsonContext.Default.StepPromotionStateArray, ct);

    public Task<Result<PromotionState>> GetProcessingStepPromotion(string stepId, CancellationToken ct) =>
        transport.GetAsync($"/api/processing-steps/{Enc(stepId)}/promotion", CliJsonContext.Default.PromotionState, ct);

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

    // Artifact bundles
    public Task<Result<ArtifactBundle[]>> ListArtifactBundles(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/artifact-bundles", CliJsonContext.Default.ArtifactBundleArray, ct);

    public Task<Result<ArtifactBundle>> GetArtifactBundle(string bundleId, CancellationToken ct) =>
        transport.GetAsync($"/api/artifact-bundles/{Enc(bundleId)}", CliJsonContext.Default.ArtifactBundle, ct);

    // Triggers
    public Task<Result<Trigger[]>> ListTriggers(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/triggers", CliJsonContext.Default.TriggerArray, ct);

    public Task<Result<Trigger>> GetTrigger(string triggerId, CancellationToken ct) =>
        transport.GetAsync($"/api/triggers/{Enc(triggerId)}", CliJsonContext.Default.Trigger, ct);

    // Secrets
    public Task<Result<string[]>> ListSecrets(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/secrets", CliJsonContext.Default.StringArray, ct);

    // Info
    public Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/config/frontend", ct);

    public Task<Result<string>> GetHealthRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/health", ct);

    public Task<Result<string>> GetReadyRaw(CancellationToken ct) =>
        transport.GetStringAsync("/api/ready", ct);

    // Mutations
    public Task<Result<JobGroup>> TriggerProduction(string pipelineId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/trigger/production",
            body: null, CliJsonContext.Default.JobGroup, ct);

    public Task<Result<PromotionState>> SetProcessingStepPromotion(string stepId, bool blocked, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"/api/processing-steps/{Enc(stepId)}/promotion",
            ApiTransport.JsonBody(new SetPromotionRequest { Blocked = blocked }, CliJsonContext.Default.SetPromotionRequest),
            CliJsonContext.Default.PromotionState, ct);

    public Task<Result<JobGroup>> RePromoteProcessingStep(string stepId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/processing-steps/{Enc(stepId)}/re-promote",
            body: null, CliJsonContext.Default.JobGroup, ct);

    public Task<Result> CancelJob(string id, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/jobs/{Enc(id)}/cancel", body: null, ct);

    public Task<Result> DeletePipeline(string id, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"/api/pipelines/{Enc(id)}", body: null, ct);

    public Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"/api/pipelines/{Enc(pipelineId)}/secrets/{Enc(name)}",
            ApiTransport.JsonBody(new SetSecretRequest { Value = value }, CliJsonContext.Default.SetSecretRequest), ct);

    public Task<Result> DeleteSecret(string pipelineId, string name, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"/api/pipelines/{Enc(pipelineId)}/secrets/{Enc(name)}", body: null, ct);
}
