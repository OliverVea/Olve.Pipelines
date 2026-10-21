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

    // Jobs
    public Result<PageOfJob>? ListJobsResult { get; set; }
    public Result<Job>? GetJobResult { get; set; }
    public Result<string>? GetJobLogsResult { get; set; }
    public Result<string>? GetJobQueueRawResult { get; set; }

    // Secrets
    public Result<string[]>? ListSecretsResult { get; set; }

    // Bindings (GitOps)
    public Result<PipelineConfigBinding>? GetPipelineBindingResult { get; set; }
    public Result<PipelineBindingStatus>? GetPipelineBindingStatusResult { get; set; }
    public Result<PipelineConfigBinding>? CreatePipelineWithRepoResult { get; set; }
    public Result<PipelineConfigBinding>? UpdatePipelineBindingCredentialsResult { get; set; }
    public Result<PipelineConfigBinding>? UpdatePipelineBindingDeployTriggerResult { get; set; }
    public Result? ReconcilePipelineBindingResult { get; set; }

    // Info
    public Result<string>? GetFrontendConfigRawResult { get; set; }
    public Result<string>? GetHealthRawResult { get; set; }
    public Result<string>? GetReadyRawResult { get; set; }

    // Mutations
    public Result? CancelJobResult { get; set; }
    public Result? DeletePipelineResult { get; set; }
    public Result? SetSecretResult { get; set; }
    public Result? DeleteSecretResult { get; set; }

    /// <summary>The value passed to the most recent <see cref="SetSecret"/> call (for assertions).</summary>
    public string? LastSecretValue { get; private set; }

    /// <summary>Arguments captured from the most recent <see cref="CreatePipelineWithRepo"/> call.</summary>
    public (string Repo, string? Branch, string? Path, string? CredentialsSecret, BindingDeployTrigger? Trigger)? LastCreateWithRepo { get; private set; }

    /// <summary>The credentials key passed to the most recent <see cref="UpdatePipelineBindingCredentials"/> call.</summary>
    public string? LastCredentialsSecret { get; private set; }

    /// <summary>The trigger passed to the most recent <see cref="UpdatePipelineBindingDeployTrigger"/> call.</summary>
    public BindingDeployTrigger? LastDeployTrigger { get; private set; }

    // Pipelines
    public Task<Result<Pipeline[]>> ListPipelines(CancellationToken ct) =>
        Record(ListPipelinesResult, nameof(ListPipelines));

    public Task<Result<PipelineSummary[]>> ListPipelineSummaries(CancellationToken ct) =>
        Record(ListPipelineSummariesResult, nameof(ListPipelineSummaries));

    public Task<Result<Pipeline>> GetPipeline(string id, CancellationToken ct) =>
        Record(GetPipelineResult, nameof(GetPipeline));

    // Jobs
    public Task<Result<PageOfJob>> ListJobs(string? pipelineId, int page, int pageSize, int sort, CancellationToken ct) =>
        Record(ListJobsResult, nameof(ListJobs));

    public Task<Result<Job>> GetJob(string id, CancellationToken ct) =>
        Record(GetJobResult, nameof(GetJob));

    public Task<Result<string>> GetJobLogs(string id, CancellationToken ct) =>
        Record(GetJobLogsResult, nameof(GetJobLogs));

    public Task<Result<string>> GetJobQueueRaw(CancellationToken ct) =>
        Record(GetJobQueueRawResult, nameof(GetJobQueueRaw));

    // Secrets
    public Task<Result<string[]>> ListSecrets(string pipelineId, CancellationToken ct) =>
        Record(ListSecretsResult, nameof(ListSecrets));

    // Bindings (GitOps)
    public Task<Result<PipelineConfigBinding>> GetPipelineBinding(string pipelineId, CancellationToken ct) =>
        Record(GetPipelineBindingResult, nameof(GetPipelineBinding));

    public Task<Result<PipelineBindingStatus>> GetPipelineBindingStatus(string pipelineId, CancellationToken ct) =>
        Record(GetPipelineBindingStatusResult, nameof(GetPipelineBindingStatus));

    // Info
    public Task<Result<string>> GetFrontendConfigRaw(CancellationToken ct) =>
        Record(GetFrontendConfigRawResult, nameof(GetFrontendConfigRaw));

    public Task<Result<string>> GetHealthRaw(CancellationToken ct) =>
        Record(GetHealthRawResult, nameof(GetHealthRaw));

    public Task<Result<string>> GetReadyRaw(CancellationToken ct) =>
        Record(GetReadyRawResult, nameof(GetReadyRaw));

    // Mutations
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

    public Task<Result> SetSecret(string pipelineId, string name, string value, CancellationToken ct)
    {
        LastSecretValue = value;
        Calls.Add($"{nameof(SetSecret)}({name})");
        return Task.FromResult(
            SetSecretResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(SetSecret)}' called but not configured."));
    }

    public Task<Result> DeleteSecret(string pipelineId, string name, CancellationToken ct)
    {
        Calls.Add($"{nameof(DeleteSecret)}({name})");
        return Task.FromResult(
            DeleteSecretResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(DeleteSecret)}' called but not configured."));
    }

    public Task<Result<PipelineConfigBinding>> CreatePipelineWithRepo(
        string repo, string? branch, string? path, string? credentialsSecret,
        BindingDeployTrigger? deployTrigger, CancellationToken ct)
    {
        LastCreateWithRepo = (repo, branch, path, credentialsSecret, deployTrigger);
        Calls.Add(nameof(CreatePipelineWithRepo));
        return Task.FromResult(
            CreatePipelineWithRepoResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(CreatePipelineWithRepo)}' called but not configured."));
    }

    public Task<Result<PipelineConfigBinding>> UpdatePipelineBindingCredentials(
        string pipelineId, string? credentialsSecret, CancellationToken ct)
    {
        LastCredentialsSecret = credentialsSecret;
        Calls.Add($"{nameof(UpdatePipelineBindingCredentials)}({credentialsSecret ?? "(null)"})");
        return Task.FromResult(
            UpdatePipelineBindingCredentialsResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(UpdatePipelineBindingCredentials)}' called but not configured."));
    }

    public Task<Result<PipelineConfigBinding>> UpdatePipelineBindingDeployTrigger(
        string pipelineId, BindingDeployTrigger deployTrigger, CancellationToken ct)
    {
        LastDeployTrigger = deployTrigger;
        Calls.Add($"{nameof(UpdatePipelineBindingDeployTrigger)}({deployTrigger})");
        return Task.FromResult(
            UpdatePipelineBindingDeployTriggerResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(UpdatePipelineBindingDeployTrigger)}' called but not configured."));
    }

    public Task<Result> ReconcilePipelineBinding(string pipelineId, CancellationToken ct)
    {
        Calls.Add(nameof(ReconcilePipelineBinding));
        return Task.FromResult(
            ReconcilePipelineBindingResult ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(ReconcilePipelineBinding)}' called but not configured."));
    }

    // Runs (v2)
    public Result<TriggerRunResponse>? TriggerRunResult { get; set; }
    public Result<PageOfRunSummary>? ListRunsResult { get; set; }
    public Result<RunDetail>? GetRunResult { get; set; }
    public Result<Run>? CancelRunResult { get; set; }
    public Result<TriggerRunResponse>? RerunRunResult { get; set; }
    public Result<Run>? ContinueRunResult { get; set; }
    public Queue<Result<Job[]>> ListStepAttemptsResults { get; } = new();
    public Result<Job>? RerunStepResult { get; set; }
    public Result<AttemptOutputResponse>? GetStepOutputResult { get; set; }
    public Result<AttemptFilesResponse>? ListStepFilesResult { get; set; }
    public byte[]? StepFileContent { get; set; }
    public Queue<Result<string>> GetStepLogsResults { get; } = new();
    public Result<StepHistoryItem[]>? GetStepHistoryResult { get; set; }
    public Result<string[]>? ListBlockedStepsResult { get; set; }
    public Result<string[]>? BlockStepResult { get; set; }
    public Result<string[]>? UnblockStepResult { get; set; }
    public Result<PipelineVersionsResponse>? ListPipelineVersionsResult { get; set; }
    public Result<PipelineVersion>? GetPipelineVersionResult { get; set; }
    public Result<PipelineActivation>? SwitchPipelineVersionResult { get; set; }
    public Result<PipelineHealth>? GetPipelineHealthResult { get; set; }
    public Result<ManifestValidationResponse>? ValidateManifestResult { get; set; }

    /// <summary>The pipeline id of the most recent run/step/gate/version call (the resolved id, never a name).</summary>
    public string? LastPipelineId { get; private set; }
    public string? LastBody { get; private set; }
    public int? LastRun { get; private set; }
    public string? LastStep { get; private set; }
    public int? LastAttempt { get; private set; }
    public string? LastPath { get; private set; }
    public bool? LastFlag { get; private set; }

    public Task<Result<TriggerRunResponse>> TriggerRun(string pipelineId, string inputJson, CancellationToken ct)
    {
        (LastPipelineId, LastBody) = (pipelineId, inputJson);
        return Record(TriggerRunResult, nameof(TriggerRun));
    }

    public Task<Result<PageOfRunSummary>> ListRuns(string pipelineId, int page, int pageSize, CancellationToken ct)
    {
        LastPipelineId = pipelineId;
        return Record(ListRunsResult, nameof(ListRuns));
    }

    public Task<Result<RunDetail>> GetRun(string pipelineId, int run, CancellationToken ct)
    {
        (LastPipelineId, LastRun) = (pipelineId, run);
        return Record(GetRunResult, nameof(GetRun));
    }

    public Task<Result<Run>> CancelRun(string pipelineId, int run, CancellationToken ct)
    {
        (LastPipelineId, LastRun) = (pipelineId, run);
        return Record(CancelRunResult, nameof(CancelRun));
    }

    public Task<Result<TriggerRunResponse>> RerunRun(string pipelineId, int run, CancellationToken ct)
    {
        (LastPipelineId, LastRun) = (pipelineId, run);
        return Record(RerunRunResult, nameof(RerunRun));
    }

    public Task<Result<Run>> ContinueRun(string pipelineId, int run, string? inputChangesJson, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastBody) = (pipelineId, run, inputChangesJson);
        return Record(ContinueRunResult, nameof(ContinueRun));
    }

    public Task<Result<Job[]>> ListStepAttempts(string pipelineId, int run, string step, CancellationToken ct)
    {
        Calls.Add(nameof(ListStepAttempts));
        return Task.FromResult(ListStepAttemptsResults.Count > 1 ? ListStepAttemptsResults.Dequeue() : ListStepAttemptsResults.Peek());
    }

    public Task<Result<Job>> RerunStep(string pipelineId, int run, string step, bool continueRun, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastStep, LastFlag) = (pipelineId, run, step, continueRun);
        return Record(RerunStepResult, nameof(RerunStep));
    }

    public Task<Result<AttemptOutputResponse>> GetStepOutput(string pipelineId, int run, string step, int? attempt, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastStep, LastAttempt) = (pipelineId, run, step, attempt);
        return Record(GetStepOutputResult, nameof(GetStepOutput));
    }

    public Task<Result<AttemptFilesResponse>> ListStepFiles(string pipelineId, int run, string step, int? attempt, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastStep, LastAttempt) = (pipelineId, run, step, attempt);
        return Record(ListStepFilesResult, nameof(ListStepFiles));
    }

    public async Task<Result<long>> DownloadStepFile(
        string pipelineId, int run, string step, string path, int? attempt, Stream destination, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastStep, LastAttempt, LastPath) = (pipelineId, run, step, attempt, path);
        Calls.Add(nameof(DownloadStepFile));
        if (StepFileContent is null)
            return new ResultProblem("Attempt has no file '{0}'.", path);
        await destination.WriteAsync(StepFileContent, ct);
        return Result.Success(StepFileContent.LongLength);
    }

    public Task<Result<string>> GetStepLogs(string pipelineId, int run, string step, int? attempt, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastStep, LastAttempt) = (pipelineId, run, step, attempt);
        Calls.Add(nameof(GetStepLogs));
        return Task.FromResult(GetStepLogsResults.Count > 1 ? GetStepLogsResults.Dequeue() : GetStepLogsResults.Peek());
    }

    public Task<Result<StepHistoryItem[]>> GetStepHistory(string pipelineId, string step, int? limit, CancellationToken ct)
    {
        (LastPipelineId, LastStep) = (pipelineId, step);
        return Record(GetStepHistoryResult, nameof(GetStepHistory));
    }

    public Task<Result<string[]>> ListBlockedSteps(string pipelineId, CancellationToken ct)
    {
        LastPipelineId = pipelineId;
        return Record(ListBlockedStepsResult, nameof(ListBlockedSteps));
    }

    public Task<Result<string[]>> BlockStep(string pipelineId, string step, CancellationToken ct)
    {
        (LastPipelineId, LastStep) = (pipelineId, step);
        return Record(BlockStepResult, nameof(BlockStep));
    }

    public Task<Result<string[]>> UnblockStep(string pipelineId, string step, CancellationToken ct)
    {
        (LastPipelineId, LastStep) = (pipelineId, step);
        return Record(UnblockStepResult, nameof(UnblockStep));
    }

    public Task<Result<PipelineVersionsResponse>> ListPipelineVersions(string pipelineId, CancellationToken ct)
    {
        LastPipelineId = pipelineId;
        return Record(ListPipelineVersionsResult, nameof(ListPipelineVersions));
    }

    public Task<Result<PipelineVersion>> GetPipelineVersion(string pipelineId, int version, CancellationToken ct)
    {
        LastPipelineId = pipelineId;
        Calls.Add($"{nameof(GetPipelineVersion)}({version})");
        return Task.FromResult(GetPipelineVersionResult
            ?? throw new NotSupportedException($"FakePipelinesApi: '{nameof(GetPipelineVersion)}' called but not configured."));
    }

    public Task<Result<PipelineActivation>> SwitchPipelineVersion(string pipelineId, int version, bool cancelRunning, CancellationToken ct)
    {
        (LastPipelineId, LastRun, LastFlag) = (pipelineId, version, cancelRunning);
        return Record(SwitchPipelineVersionResult, nameof(SwitchPipelineVersion));
    }

    public Task<Result<PipelineHealth>> GetPipelineHealth(string pipelineId, CancellationToken ct)
    {
        LastPipelineId = pipelineId;
        return Record(GetPipelineHealthResult, nameof(GetPipelineHealth));
    }

    public Task<Result<ManifestValidationResponse>> ValidateManifest(string manifestJson, CancellationToken ct)
    {
        LastBody = manifestJson;
        return Record(ValidateManifestResult, nameof(ValidateManifest));
    }

    private Task<Result<T>> Record<T>(Result<T>? configured, string method)
    {
        Calls.Add(method);
        return Task.FromResult(
            configured ?? throw new NotSupportedException($"FakePipelinesApi: '{method}' called but not configured."));
    }
}
