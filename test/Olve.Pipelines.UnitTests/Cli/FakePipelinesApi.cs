using Olve.Results;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.UnitTests.Cli;

/// <summary>
/// Hand-rolled <see cref="IPipelinesApi"/> double (mirrors the FakeProcessRunner style — no Rocks/DI).
/// Records each call and returns a scripted result; an unconfigured call throws so tests fail loudly.
/// Grows alongside <see cref="IPipelinesApi"/>.
/// </summary>
public sealed class FakePipelinesApi : IPipelinesApi
{
    public List<string> Calls { get; } = [];

    public bool Invoked(string method) => Calls.Contains(method);

    // Pipelines
    public Result<Pipeline[]>? ListPipelinesResult { get; set; }
    public Result<PipelineSummary[]>? ListPipelineSummariesResult { get; set; }
    public Result<Pipeline>? GetPipelineResult { get; set; }
    public Result<string>? GetPipelineDocumentRawResult { get; set; }

    // Production steps
    public Result<ProductionStep[]>? ListProductionStepsResult { get; set; }
    public Result<ProductionStep>? GetProductionStepResult { get; set; }
    public Result<StepConfiguration>? GetProductionStepConfigurationResult { get; set; }

    // Processing steps
    public Result<ProcessingStep[]>? ListProcessingStepsResult { get; set; }
    public Result<ProcessingStep>? GetProcessingStepResult { get; set; }
    public Result<StepConfiguration>? GetProcessingStepConfigurationResult { get; set; }
    public Result<StepPromotionState[]>? ListProcessingStepPromotionsResult { get; set; }
    public Result<PromotionState>? GetProcessingStepPromotionResult { get; set; }

    // Jobs
    public Result<PageOfJob>? ListJobsResult { get; set; }
    public Result<Job>? GetJobResult { get; set; }
    public Result<string>? GetJobLogsResult { get; set; }
    public Result<string>? GetJobQueueRawResult { get; set; }

    // Artifact bundles
    public Result<ArtifactBundle[]>? ListArtifactBundlesResult { get; set; }
    public Result<ArtifactBundle>? GetArtifactBundleResult { get; set; }

    // Triggers
    public Result<Trigger[]>? ListTriggersResult { get; set; }
    public Result<Trigger>? GetTriggerResult { get; set; }

    // Secrets
    public Result<string[]>? ListSecretsResult { get; set; }

    // Info
    public Result<string>? GetFrontendConfigRawResult { get; set; }
    public Result<string>? GetHealthRawResult { get; set; }
    public Result<string>? GetReadyRawResult { get; set; }

    // Mutations
    public Result<JobGroup>? TriggerProductionResult { get; set; }
    public Result<PromotionState>? SetProcessingStepPromotionResult { get; set; }
    public Result<JobGroup>? RePromoteProcessingStepResult { get; set; }
    public Result? CancelJobResult { get; set; }
    public Result? DeletePipelineResult { get; set; }

    // Pipelines
    public Task<Result<Pipeline[]>> ListPipelines(CancellationToken ct) =>
        Record(ListPipelinesResult, nameof(ListPipelines));

    public Task<Result<PipelineSummary[]>> ListPipelineSummaries(CancellationToken ct) =>
        Record(ListPipelineSummariesResult, nameof(ListPipelineSummaries));

    public Task<Result<Pipeline>> GetPipeline(string id, CancellationToken ct) =>
        Record(GetPipelineResult, nameof(GetPipeline));

    public Task<Result<string>> GetPipelineDocumentRaw(string id, CancellationToken ct) =>
        Record(GetPipelineDocumentRawResult, nameof(GetPipelineDocumentRaw));

    // Production steps
    public Task<Result<ProductionStep[]>> ListProductionSteps(string pipelineId, CancellationToken ct) =>
        Record(ListProductionStepsResult, nameof(ListProductionSteps));

    public Task<Result<ProductionStep>> GetProductionStep(string stepId, CancellationToken ct) =>
        Record(GetProductionStepResult, nameof(GetProductionStep));

    public Task<Result<StepConfiguration>> GetProductionStepConfiguration(string stepId, CancellationToken ct) =>
        Record(GetProductionStepConfigurationResult, nameof(GetProductionStepConfiguration));

    // Processing steps
    public Task<Result<ProcessingStep[]>> ListProcessingSteps(string pipelineId, CancellationToken ct) =>
        Record(ListProcessingStepsResult, nameof(ListProcessingSteps));

    public Task<Result<ProcessingStep>> GetProcessingStep(string stepId, CancellationToken ct) =>
        Record(GetProcessingStepResult, nameof(GetProcessingStep));

    public Task<Result<StepConfiguration>> GetProcessingStepConfiguration(string stepId, CancellationToken ct) =>
        Record(GetProcessingStepConfigurationResult, nameof(GetProcessingStepConfiguration));

    public Task<Result<StepPromotionState[]>> ListProcessingStepPromotions(string pipelineId, CancellationToken ct) =>
        Record(ListProcessingStepPromotionsResult, nameof(ListProcessingStepPromotions));

    public Task<Result<PromotionState>> GetProcessingStepPromotion(string stepId, CancellationToken ct) =>
        Record(GetProcessingStepPromotionResult, nameof(GetProcessingStepPromotion));

    // Jobs
    public Task<Result<PageOfJob>> ListJobs(string? pipelineId, int page, int pageSize, int sort, CancellationToken ct) =>
        Record(ListJobsResult, nameof(ListJobs));

    public Task<Result<Job>> GetJob(string id, CancellationToken ct) =>
        Record(GetJobResult, nameof(GetJob));

    public Task<Result<string>> GetJobLogs(string id, CancellationToken ct) =>
        Record(GetJobLogsResult, nameof(GetJobLogs));

    public Task<Result<string>> GetJobQueueRaw(CancellationToken ct) =>
        Record(GetJobQueueRawResult, nameof(GetJobQueueRaw));

    // Artifact bundles
    public Task<Result<ArtifactBundle[]>> ListArtifactBundles(string pipelineId, CancellationToken ct) =>
        Record(ListArtifactBundlesResult, nameof(ListArtifactBundles));

    public Task<Result<ArtifactBundle>> GetArtifactBundle(string bundleId, CancellationToken ct) =>
        Record(GetArtifactBundleResult, nameof(GetArtifactBundle));

    // Triggers
    public Task<Result<Trigger[]>> ListTriggers(string pipelineId, CancellationToken ct) =>
        Record(ListTriggersResult, nameof(ListTriggers));

    public Task<Result<Trigger>> GetTrigger(string triggerId, CancellationToken ct) =>
        Record(GetTriggerResult, nameof(GetTrigger));

    // Secrets
    public Task<Result<string[]>> ListSecrets(string pipelineId, CancellationToken ct) =>
        Record(ListSecretsResult, nameof(ListSecrets));

    // Info
    public Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct) =>
        Record(GetFrontendConfigRawResult, nameof(GetFrontendConfigRaw));

    public Task<Result<string>> GetHealthRaw(CancellationToken ct) =>
        Record(GetHealthRawResult, nameof(GetHealthRaw));

    public Task<Result<string>> GetReadyRaw(CancellationToken ct) =>
        Record(GetReadyRawResult, nameof(GetReadyRaw));

    // Mutations
    public Task<Result<JobGroup>> TriggerProduction(string pipelineId, CancellationToken ct) =>
        Record(TriggerProductionResult, nameof(TriggerProduction));

    public Task<Result<PromotionState>> SetProcessingStepPromotion(string stepId, bool blocked, CancellationToken ct)
    {
        Calls.Add($"{nameof(SetProcessingStepPromotion)}({blocked})");
        return Task.FromResult(
            SetProcessingStepPromotionResult
            ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(SetProcessingStepPromotion)}' called but not configured."));
    }

    public Task<Result<JobGroup>> RePromoteProcessingStep(string stepId, CancellationToken ct) =>
        Record(RePromoteProcessingStepResult, nameof(RePromoteProcessingStep));

    public Task<Result> CancelJob(string id, CancellationToken ct)
    {
        Calls.Add(nameof(CancelJob));
        return Task.FromResult(
            CancelJobResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(CancelJob)}' called but not configured."));
    }

    public Task<Result> DeletePipeline(string id, CancellationToken ct)
    {
        Calls.Add(nameof(DeletePipeline));
        return Task.FromResult(
            DeletePipelineResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(DeletePipeline)}' called but not configured."));
    }

    private Task<Result<T>> Record<T>(Result<T>? configured, string method)
    {
        Calls.Add(method);
        return Task.FromResult(
            configured ?? throw new NotSupportedException($"FakePipelinesApi: '{method}' called but not configured."));
    }
}
