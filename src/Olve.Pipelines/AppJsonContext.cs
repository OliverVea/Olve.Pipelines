using System.Text.Json.Serialization;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Problems;
using Olve.Utilities.Paginations;

namespace Olve.Pipelines;

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(Result<Pipeline>))]
[JsonSerializable(typeof(Result<Pipeline[]>))]
[JsonSerializable(typeof(Result<PipelineSummary[]>))]
[JsonSerializable(typeof(Result<string>))]
[JsonSerializable(typeof(Result<string[]>))]
[JsonSerializable(typeof(Result<Attempt>))]
[JsonSerializable(typeof(Result<Attempt[]>))]
[JsonSerializable(typeof(Result<ActiveAttempt[]>))]
[JsonSerializable(typeof(Result<Run>))]
[JsonSerializable(typeof(Result<Page<RunSummary>>))]
[JsonSerializable(typeof(Result<RunDetail>))]
[JsonSerializable(typeof(Result<StepHistoryItem[]>))]
[JsonSerializable(typeof(Result<CriticalPathReport>))]
[JsonSerializable(typeof(Result<AttemptFilesResponse>))]
[JsonSerializable(typeof(Result<PipelineHealth>))]
[JsonSerializable(typeof(RerunStepRequest))]
[JsonSerializable(typeof(AnswerInputRequest))]
[JsonSerializable(typeof(DeclineInputRequest))]
[JsonSerializable(typeof(ContinueRunRequest))]
[JsonSerializable(typeof(TriggerRunRequest))]
[JsonSerializable(typeof(ManifestValidationResponse))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(Result<TriggerRunResponse>))]
[JsonSerializable(typeof(Result<AttemptOutputResponse>))]
[JsonSerializable(typeof(Result<PipelineVersionsResponse>))]
[JsonSerializable(typeof(Result<PipelineVersion>))]
[JsonSerializable(typeof(Result<EffectivePipelineResponse>))]
[JsonSerializable(typeof(Olve.Pipelines.Health.HealthResponse))]
[JsonSerializable(typeof(Result<PipelineActivation>))]
[JsonSerializable(typeof(SwitchVersionRequest))]
[JsonSerializable(typeof(PipelineConfigBinding))]
[JsonSerializable(typeof(Result<PipelineConfigBinding>))]
[JsonSerializable(typeof(CreatePipelineWithRepoRequest))]
[JsonSerializable(typeof(UpdateBindingRequest))]
[JsonSerializable(typeof(SetBindingBranchRequest))]
[JsonSerializable(typeof(SetBindingPollingRequest))]
[JsonSerializable(typeof(Result<PipelineBindingStatus>))]
[JsonSerializable(typeof(DeletionResult))]
[JsonSerializable(typeof(SetSecretRequest))]
[JsonSerializable(typeof(CopySecretsRequest))]
[JsonSerializable(typeof(CopySecretsResponse))]
[JsonSerializable(typeof(Result<CopySecretsResponse>))]
[JsonSerializable(typeof(Result<PipelineSecretInfo[]>))]
[JsonSerializable(typeof(FrontendConfigEndpoints.FrontendConfig))]
[JsonSerializable(typeof(Result<ConfigDocument>))]
[JsonSerializable(typeof(Result<ConfigSaveResponse>))]
[JsonSerializable(typeof(Result<ConfigSnapshotInfo[]>))]
[JsonSerializable(typeof(Result<ConfigCheckResponse>))]
[JsonSerializable(typeof(SetConfigRequest))]
[JsonSerializable(typeof(RollbackConfigRequest))]
internal partial class AppJsonContext : JsonSerializerContext;
