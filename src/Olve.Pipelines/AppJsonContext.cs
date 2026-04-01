using System.Text.Json.Serialization;
using Olve.Pipelines.Building;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.PipelineBuilders.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.PipelineSources.Api;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Processing.Api;
using Olve.Pipelines.Sourcing;

namespace Olve.Pipelines;

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(ResultProblem[]))]
[JsonSerializable(typeof(Result<Pipeline>))]
[JsonSerializable(typeof(Result<Pipeline[]>))]
[JsonSerializable(typeof(Result<PipelineSource>))]
[JsonSerializable(typeof(Result<PipelineSource[]>))]
[JsonSerializable(typeof(PipelineSourceEndpoints.CreatePipelineSourceRequest))]
[JsonSerializable(typeof(Result<PipelineBuilder>))]
[JsonSerializable(typeof(Result<PipelineBuilder[]>))]
[JsonSerializable(typeof(PipelineBuilderEndpoints.CreatePipelineBuilderRequest))]
[JsonSerializable(typeof(Result<ProcessingStep>))]
[JsonSerializable(typeof(Result<ProcessingStep[]>))]
[JsonSerializable(typeof(ProcessingEndpoints.CreateProcessingStepRequest))]
[JsonSerializable(typeof(Result<Verification>))]
[JsonSerializable(typeof(Result<Verification[]>))]
[JsonSerializable(typeof(ProcessingEndpoints.CreateVerificationRequest))]
[JsonSerializable(typeof(Result<HardcodedSource>))]
[JsonSerializable(typeof(PipelineSourceEndpoints.SetHardcodedSourceRequest))]
[JsonSerializable(typeof(Result<GitHubSource>))]
[JsonSerializable(typeof(PipelineSourceEndpoints.SetGitHubSourceRequest))]
[JsonSerializable(typeof(Result<ScriptBuilder>))]
[JsonSerializable(typeof(PipelineBuilderEndpoints.SetScriptBuilderRequest))]
[JsonSerializable(typeof(Result<ScriptProcessing>))]
[JsonSerializable(typeof(ProcessingEndpoints.SetScriptProcessingRequest))]
[JsonSerializable(typeof(Result<ScriptVerification>))]
[JsonSerializable(typeof(ProcessingEndpoints.SetScriptVerificationRequest))]
[JsonSerializable(typeof(Result<SourceBundle>))]
[JsonSerializable(typeof(Result<SourceBundle[]>))]
[JsonSerializable(typeof(Result<ArtifactBundle>))]
[JsonSerializable(typeof(Result<ArtifactBundle[]>))]
[JsonSerializable(typeof(Result<string>))]
[JsonSerializable(typeof(Result<string[]>))]
[JsonSerializable(typeof(DeletionResult))]
[JsonSerializable(typeof(SetSecretRequest))]
internal partial class AppJsonContext : JsonSerializerContext;
