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
    Task<Result<string>> GetPipelineDocumentRaw(string id, CancellationToken ct);

    // Production steps
    Task<Result<ProductionStep[]>> ListProductionSteps(string pipelineId, CancellationToken ct);
    Task<Result<ProductionStep>> GetProductionStep(string stepId, CancellationToken ct);
    Task<Result<StepConfiguration>> GetProductionStepConfiguration(string stepId, CancellationToken ct);

    // Processing steps
    Task<Result<ProcessingStep[]>> ListProcessingSteps(string pipelineId, CancellationToken ct);
    Task<Result<ProcessingStep>> GetProcessingStep(string stepId, CancellationToken ct);
    Task<Result<StepConfiguration>> GetProcessingStepConfiguration(string stepId, CancellationToken ct);
    Task<Result<StepPromotionState[]>> ListProcessingStepPromotions(string pipelineId, CancellationToken ct);
    Task<Result<PromotionState>> GetProcessingStepPromotion(string stepId, CancellationToken ct);

    // Jobs
    Task<Result<PageOfJob>> ListJobs(string? pipelineId, int page, int pageSize, int sort, CancellationToken ct);
    Task<Result<Job>> GetJob(string id, CancellationToken ct);
    Task<Result<string>> GetJobLogs(string id, CancellationToken ct);
    Task<Result<string>> GetJobQueueRaw(CancellationToken ct);

    // Artifact bundles
    Task<Result<ArtifactBundle[]>> ListArtifactBundles(string pipelineId, CancellationToken ct);
    Task<Result<ArtifactBundle>> GetArtifactBundle(string bundleId, CancellationToken ct);

    // Triggers
    Task<Result<Trigger[]>> ListTriggers(string pipelineId, CancellationToken ct);
    Task<Result<Trigger>> GetTrigger(string triggerId, CancellationToken ct);

    // Secrets
    Task<Result<string[]>> ListSecrets(string pipelineId, CancellationToken ct);

    // Info
    Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct);
    Task<Result<string>> GetHealthRaw(CancellationToken ct);
    Task<Result<string>> GetReadyRaw(CancellationToken ct);

    // Mutations
    Task<Result<JobGroup>> TriggerProduction(string pipelineId, CancellationToken ct);
    Task<Result<PromotionState>> SetProcessingStepPromotion(string stepId, bool blocked, CancellationToken ct);
    Task<Result<JobGroup>> RePromoteProcessingStep(string stepId, CancellationToken ct);
    Task<Result> CancelJob(string id, CancellationToken ct);
    Task<Result> DeletePipeline(string id, CancellationToken ct);
    Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct);
    Task<Result> DeleteSecret(string pipelineId, string name, CancellationToken ct);
}
