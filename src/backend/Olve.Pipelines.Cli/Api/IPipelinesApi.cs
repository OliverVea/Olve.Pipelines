using System.Text.Json.Nodes;
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
    Task<Result<Pipeline>> GetPipeline(string id, CancellationToken ct);

    // Attempts across pipelines (what's running)
    Task<Result<ActiveAttempt[]>> ListActiveAttempts(CancellationToken ct);
    Task<Result<ActiveAttempt[]>> ListWaitingAttempts(CancellationToken ct);

    // Secrets
    Task<Result<PipelineSecretInfo[]>> ListSecrets(string pipelineId, CancellationToken ct);

    // Bindings (GitOps)
    Task<Result<PipelineConfigBinding>> GetPipelineBinding(string pipelineId, CancellationToken ct);
    Task<Result<PipelineBindingStatus>> GetPipelineBindingStatus(string pipelineId, CancellationToken ct);

    // Info
    Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct);
    Task<Result<string>> GetHealthRaw(CancellationToken ct);
    Task<Result<string>> GetReadyRaw(CancellationToken ct);

    // Mutations
    Task<Result> DeletePipeline(string id, CancellationToken ct);
    Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct);
    Task<Result> DeleteSecret(string pipelineId, string name, bool force, CancellationToken ct);
    Task<Result<CopySecretsResponse>> CopySecrets(string targetPipelineId, CopySecretsRequest request, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> CreatePipelineWithRepo(
        string repo, string? branch, string? path, string? credentialsSecret, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> UpdatePipelineBindingCredentials(
        string pipelineId, string? credentialsSecret, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> SetPipelineBindingBranch(string pipelineId, string branch, CancellationToken ct);
    Task<Result<PipelineConfigBinding>> SetPipelineBindingPolling(string pipelineId, bool polls, CancellationToken ct);
    Task<Result> ReconcilePipelineBinding(string pipelineId, bool rebuild, CancellationToken ct);

    // Config (one YAML document per pipeline, saved as snapshots)
    Task<Result<ConfigDocument>> GetPipelineConfig(string pipelineId, int? snapshot, CancellationToken ct);
    Task<Result<ConfigSaveResponse>> SetPipelineConfig(string pipelineId, string yaml, CancellationToken ct);
    Task<Result<ConfigSnapshotInfo[]>> ListPipelineConfigSnapshots(string pipelineId, CancellationToken ct);
    Task<Result<ConfigSaveResponse>> RollbackPipelineConfig(string pipelineId, int snapshot, CancellationToken ct);
    Task<Result<ConfigCheckResponse>> CheckPipelineConfig(string pipelineId, int? version, CancellationToken ct);
    Task<Result<EffectivePipelineResponse>> GetEffectivePipeline(string pipelineId, int version, CancellationToken ct);

    // Runs (v2, spec §5) — pipelineId is always the resolved id; runs are addressed by number.
    Task<Result<TriggerRunResponse>> TriggerRun(string pipelineId, string inputJson, CancellationToken ct);
    Task<Result<PageOfRunSummary>> ListRuns(string pipelineId, int page, int pageSize, CancellationToken ct);
    Task<Result<CriticalPathReport>> GetCriticalPath(string pipelineId, int? from, int? to, bool includeUnsuccessful, int? sinceVersion, CancellationToken ct);
    Task<Result<RunDetail>> GetRun(string pipelineId, int run, CancellationToken ct);
    Task<Result<Run>> CancelRun(string pipelineId, int run, CancellationToken ct);
    Task<Result<TriggerRunResponse>> RerunRun(string pipelineId, int run, CancellationToken ct);
    Task<Result<Run>> ContinueRun(string pipelineId, int run, string? inputChangesJson, CancellationToken ct);

    // Steps of a run (attempt null = latest successful for output/files, latest for logs)
    Task<Result<Attempt[]>> ListStepAttempts(string pipelineId, int run, string step, CancellationToken ct);
    Task<Result<Attempt>> RerunStep(string pipelineId, int run, string step, bool continueRun, CancellationToken ct);
    Task<Result<Attempt>> CancelStep(string pipelineId, int run, string step, CancellationToken ct);
    Task<Result<Attempt>> AnswerStepInput(string pipelineId, int run, string step, string? name, JsonNode value, CancellationToken ct);
    Task<Result<Attempt>> DeclineStepInput(string pipelineId, int run, string step, string? comment, CancellationToken ct);
    Task<Result<AttemptOutputResponse>> GetStepOutput(string pipelineId, int run, string step, int? attempt, CancellationToken ct);
    Task<Result<AttemptFilesResponse>> ListStepFiles(string pipelineId, int run, string step, int? attempt, CancellationToken ct);
    Task<Result<long>> DownloadStepFile(string pipelineId, int run, string step, string path, int? attempt, Stream destination, CancellationToken ct);
    Task<Result<string>> GetStepLogs(string pipelineId, int run, string step, int? attempt, CancellationToken ct);
    Task<Result<StepHistoryItem[]>> GetStepHistory(string pipelineId, string step, int? limit, CancellationToken ct);

    // Live change notifications (SSE): onEvent(type, data) until the stream ends
    Task<Result> WatchEvents(string? pipelineId, Func<string, string, Task> onEvent, CancellationToken ct);

    // Blocked steps (keyed on step name)
    Task<Result<string[]>> ListBlockedSteps(string pipelineId, CancellationToken ct);
    Task<Result<string[]>> BlockStep(string pipelineId, string step, CancellationToken ct);
    Task<Result<string[]>> UnblockStep(string pipelineId, string step, CancellationToken ct);

    // Versions, health, manifests
    Task<Result<PipelineVersionsResponse>> ListPipelineVersions(string pipelineId, CancellationToken ct);
    Task<Result<PipelineVersion>> GetPipelineVersion(string pipelineId, int version, CancellationToken ct);
    Task<Result<PipelineActivation>> SwitchPipelineVersion(string pipelineId, int version, bool cancelRunning, CancellationToken ct);
    Task<Result<PipelineHealth>> GetPipelineHealth(string pipelineId, CancellationToken ct);
    Task<Result<ManifestValidationResponse>> ValidateManifest(string manifestJson, CancellationToken ct);
}
