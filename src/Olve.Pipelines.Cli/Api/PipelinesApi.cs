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

    // Attempts across pipelines
    public Task<Result<ActiveAttempt[]>> ListActiveAttempts(CancellationToken ct) =>
        transport.GetAsync("/api/attempts?status=active", CliJsonContext.Default.ActiveAttemptArray, ct);

    // Secrets
    public Task<Result<PipelineSecretInfo[]>> ListSecrets(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/secrets", CliJsonContext.Default.PipelineSecretInfoArray, ct);

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
    public Task<Result> DeletePipeline(string id, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"/api/pipelines/{Enc(id)}", body: null, ct);

    public Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"/api/pipelines/{Enc(pipelineId)}/secrets/{Enc(name)}",
            ApiTransport.JsonBody(new SetSecretRequest { Value = value }, CliJsonContext.Default.SetSecretRequest), ct);

    public Task<Result> DeleteSecret(string pipelineId, string name, bool force, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"/api/pipelines/{Enc(pipelineId)}/secrets/{Enc(name)}{(force ? "?force=true" : "")}", body: null, ct);

    // Config
    public Task<Result<ConfigDocument>> GetPipelineConfig(string pipelineId, int? snapshot, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/config{(snapshot is { } n ? $"?snapshot={n}" : "")}", CliJsonContext.Default.ConfigDocument, ct);

    public Task<Result<ConfigSaveResponse>> SetPipelineConfig(string pipelineId, string yaml, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"/api/pipelines/{Enc(pipelineId)}/config",
            ApiTransport.JsonBody(new SetConfigRequest { Yaml = yaml }, CliJsonContext.Default.SetConfigRequest), CliJsonContext.Default.ConfigSaveResponse, ct);

    public Task<Result<ConfigSnapshotInfo[]>> ListPipelineConfigSnapshots(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/config/history", CliJsonContext.Default.ConfigSnapshotInfoArray, ct);

    public Task<Result<ConfigSaveResponse>> RollbackPipelineConfig(string pipelineId, int snapshot, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/config/rollback",
            ApiTransport.JsonBody(new RollbackConfigRequest { Snapshot = snapshot }, CliJsonContext.Default.RollbackConfigRequest), CliJsonContext.Default.ConfigSaveResponse, ct);

    public Task<Result<EffectivePipelineResponse>> GetEffectivePipeline(string pipelineId, int version, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/versions/{version}/effective", CliJsonContext.Default.EffectivePipelineResponse, ct);

    public Task<Result<ConfigCheckResponse>> CheckPipelineConfig(string pipelineId, int? version, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/config/check{(version is { } v ? $"?version={v}" : "")}", CliJsonContext.Default.ConfigCheckResponse, ct);

    public Task<Result<CopySecretsResponse>> CopySecrets(string targetPipelineId, CopySecretsRequest request, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(targetPipelineId)}/secrets/copy",
            ApiTransport.JsonBody(request, CliJsonContext.Default.CopySecretsRequest), CliJsonContext.Default.CopySecretsResponse, ct);

    public Task<Result<PipelineConfigBinding>> CreatePipelineWithRepo(
        string repo, string? branch, string? path, string? credentialsSecret, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, "/api/pipelines/with-repo",
            ApiTransport.JsonBody(
                new CreatePipelineWithRepoRequest
                {
                    Repo = repo, Branch = branch, Path = path, CredentialsSecret = credentialsSecret,
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

    public Task<Result<PipelineConfigBinding>> SetPipelineBindingBranch(
        string pipelineId, string branch, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"/api/pipelines/{Enc(pipelineId)}/binding/branch",
            ApiTransport.JsonBody(new SetBindingBranchRequest { Branch = branch }, CliJsonContext.Default.SetBindingBranchRequest),
            CliJsonContext.Default.PipelineConfigBinding, ct);

    public Task<Result> ReconcilePipelineBinding(string pipelineId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/binding/reconcile", body: null, ct);

    // Runs
    private static string Runs(string pipelineId) => $"/api/pipelines/{Enc(pipelineId)}/runs";

    private static string StepPath(string pipelineId, int run, string step) => $"{Runs(pipelineId)}/{run}/steps/{Enc(step)}";

    private static string AttemptQuery(int? attempt) => attempt is { } n ? $"?attempt={n}" : "";

    public Task<Result<TriggerRunResponse>> TriggerRun(string pipelineId, string inputJson, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, Runs(pipelineId), ApiTransport.RawJson(inputJson),
            CliJsonContext.Default.TriggerRunResponse, ct);

    public Task<Result<PageOfRunSummary>> ListRuns(string pipelineId, int page, int pageSize, CancellationToken ct) =>
        transport.GetAsync($"{Runs(pipelineId)}?Page={page}&PageSize={pageSize}", CliJsonContext.Default.PageOfRunSummary, ct);

    public Task<Result<CriticalPathReport>> GetCriticalPath(string pipelineId, int? from, int? to, bool includeUnsuccessful, int? sinceVersion, CancellationToken ct)
    {
        var query = string.Join("&", new[]
        {
            from is { } f ? $"from={f}" : null,
            sinceVersion is { } v ? $"sinceVersion={v}" : null,
            to is { } t ? $"to={t}" : null,
            includeUnsuccessful ? "includeUnsuccessful=true" : null,
        }.Where(q => q is not null));
        return transport.GetAsync($"{Runs(pipelineId)}/critical-path{(query.Length > 0 ? "?" + query : "")}",
            CliJsonContext.Default.CriticalPathReport, ct);
    }

    public Task<Result<RunDetail>> GetRun(string pipelineId, int run, CancellationToken ct) =>
        transport.GetAsync($"{Runs(pipelineId)}/{run}", CliJsonContext.Default.RunDetail, ct);

    public Task<Result<Run>> CancelRun(string pipelineId, int run, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"{Runs(pipelineId)}/{run}/cancel", body: null, CliJsonContext.Default.Run, ct);

    public Task<Result<TriggerRunResponse>> RerunRun(string pipelineId, int run, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"{Runs(pipelineId)}/{run}/rerun", body: null, CliJsonContext.Default.TriggerRunResponse, ct);

    // Continue's body is {"input": {changed fields}} (or {} for none); the changes are spliced in as raw JSON.
    public Task<Result<Run>> ContinueRun(string pipelineId, int run, string? inputChangesJson, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"{Runs(pipelineId)}/{run}/continue",
            ApiTransport.RawJson(inputChangesJson is null ? "{}" : $"{{\"input\":{inputChangesJson}}}"),
            CliJsonContext.Default.Run, ct);

    public Task<Result<Attempt[]>> ListStepAttempts(string pipelineId, int run, string step, CancellationToken ct) =>
        transport.GetAsync($"{StepPath(pipelineId, run, step)}/attempts", CliJsonContext.Default.AttemptArray, ct);

    public Task<Result<Attempt>> CancelStep(string pipelineId, int run, string step, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"{StepPath(pipelineId, run, step)}/cancel", body: null, CliJsonContext.Default.Attempt, ct);

    public Task<Result<Attempt>> RerunStep(string pipelineId, int run, string step, bool continueRun, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"{StepPath(pipelineId, run, step)}/rerun",
            ApiTransport.JsonBody(new RerunStepRequest { Continue = continueRun }, CliJsonContext.Default.RerunStepRequest),
            CliJsonContext.Default.Attempt, ct);

    public Task<Result<AttemptOutputResponse>> GetStepOutput(string pipelineId, int run, string step, int? attempt, CancellationToken ct) =>
        transport.GetAsync($"{StepPath(pipelineId, run, step)}/output{AttemptQuery(attempt)}", CliJsonContext.Default.AttemptOutputResponse, ct);

    public Task<Result<AttemptFilesResponse>> ListStepFiles(string pipelineId, int run, string step, int? attempt, CancellationToken ct) =>
        transport.GetAsync($"{StepPath(pipelineId, run, step)}/files{AttemptQuery(attempt)}", CliJsonContext.Default.AttemptFilesResponse, ct);

    // The file route is a catch-all ({*path}): escape each segment, keep the slashes.
    public Task<Result<long>> DownloadStepFile(
        string pipelineId, int run, string step, string path, int? attempt, Stream destination, CancellationToken ct) =>
        transport.DownloadAsync(
            $"{StepPath(pipelineId, run, step)}/files/{string.Join('/', path.Split('/').Select(Enc))}{AttemptQuery(attempt)}",
            destination, ct);

    public Task<Result<string>> GetStepLogs(string pipelineId, int run, string step, int? attempt, CancellationToken ct) =>
        transport.GetAsync($"{StepPath(pipelineId, run, step)}/logs{AttemptQuery(attempt)}", CliJsonContext.Default.String, ct);

    public Task<Result> WatchEvents(string? pipelineId, Func<string, string, Task> onEvent, CancellationToken ct) =>
        transport.ReadEventsAsync(pipelineId is null ? "/api/events" : $"/api/events?pipeline={Enc(pipelineId)}", onEvent, ct);

    public Task<Result<StepHistoryItem[]>> GetStepHistory(string pipelineId, string step, int? limit, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/steps/{Enc(step)}/history{(limit is { } l ? $"?limit={l}" : "")}",
            CliJsonContext.Default.StepHistoryItemArray, ct);

    // Blocked steps
    public Task<Result<string[]>> ListBlockedSteps(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/blocked-steps", CliJsonContext.Default.StringArray, ct);

    public Task<Result<string[]>> BlockStep(string pipelineId, string step, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/steps/{Enc(step)}/block", body: null,
            CliJsonContext.Default.StringArray, ct);

    public Task<Result<string[]>> UnblockStep(string pipelineId, string step, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/steps/{Enc(step)}/unblock", body: null,
            CliJsonContext.Default.StringArray, ct);

    // Versions, health, manifests
    public Task<Result<PipelineVersionsResponse>> ListPipelineVersions(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/versions", CliJsonContext.Default.PipelineVersionsResponse, ct);

    public Task<Result<PipelineVersion>> GetPipelineVersion(string pipelineId, int version, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/versions/{version}", CliJsonContext.Default.PipelineVersion, ct);

    public Task<Result<PipelineActivation>> SwitchPipelineVersion(string pipelineId, int version, bool cancelRunning, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"/api/pipelines/{Enc(pipelineId)}/switch",
            ApiTransport.JsonBody(new SwitchVersionRequest { Version = version, CancelRunning = cancelRunning },
                CliJsonContext.Default.SwitchVersionRequest),
            CliJsonContext.Default.PipelineActivation, ct);

    public Task<Result<PipelineHealth>> GetPipelineHealth(string pipelineId, CancellationToken ct) =>
        transport.GetAsync($"/api/pipelines/{Enc(pipelineId)}/health", CliJsonContext.Default.PipelineHealth, ct);

    public Task<Result<ManifestValidationResponse>> ValidateManifest(string manifestJson, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, "/api/manifests/validate", ApiTransport.RawJson(manifestJson),
            CliJsonContext.Default.ManifestValidationResponse, ct);
}
