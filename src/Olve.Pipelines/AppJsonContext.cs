using System.Text.Json.Serialization;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Utilities.Paginations;

namespace Olve.Pipelines;

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(ResultProblem[]))]
[JsonSerializable(typeof(Result<Pipeline>))]
[JsonSerializable(typeof(Result<Pipeline[]>))]
[JsonSerializable(typeof(Result<PipelineSummary[]>))]
[JsonSerializable(typeof(Result<string>))]
[JsonSerializable(typeof(Result<string[]>))]
[JsonSerializable(typeof(Result<Attempt>))]
[JsonSerializable(typeof(Result<Attempt[]>))]
[JsonSerializable(typeof(Result<Page<Attempt>>))]
[JsonSerializable(typeof(AttemptSortField))]
[JsonSerializable(typeof(Result<Id<Attempt>[]>))]
[JsonSerializable(typeof(Result<Run>))]
[JsonSerializable(typeof(Result<Run[]>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(Result<TriggerRunResponse>))]
[JsonSerializable(typeof(Result<AttemptOutputResponse>))]
[JsonSerializable(typeof(Result<PipelineVersionsResponse>))]
[JsonSerializable(typeof(Result<PipelineVersion>))]
[JsonSerializable(typeof(Result<PipelineActivation>))]
[JsonSerializable(typeof(SwitchVersionRequest))]
[JsonSerializable(typeof(PipelineConfigBinding))]
[JsonSerializable(typeof(Result<PipelineConfigBinding>))]
[JsonSerializable(typeof(CreatePipelineWithRepoRequest))]
[JsonSerializable(typeof(UpdateBindingRequest))]
[JsonSerializable(typeof(SetDeployTriggerRequest))]
[JsonSerializable(typeof(Result<PipelineBindingStatus>))]
[JsonSerializable(typeof(DeletionResult))]
[JsonSerializable(typeof(SetSecretRequest))]
[JsonSerializable(typeof(FrontendConfigEndpoints.FrontendConfig))]
internal partial class AppJsonContext : JsonSerializerContext;
