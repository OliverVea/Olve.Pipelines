using System.Text.Json.Serialization;
using Olve.Pipelines.PipelineArtifacts;
using Olve.Pipelines.PipelineArtifacts.Api;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.PipelineBuilders.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.PipelineSources.Api;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Processing.Api;
using Olve.Results;

namespace Olve.Pipelines;

[JsonSerializable(typeof(Guid))]
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
[JsonSerializable(typeof(Result<PipelineArtifact>))]
[JsonSerializable(typeof(Result<PipelineArtifact[]>))]
[JsonSerializable(typeof(PipelineArtifactEndpoints.CreatePipelineArtifactRequest))]
[JsonSerializable(typeof(Result<ProcessingStep>))]
[JsonSerializable(typeof(Result<ProcessingStep[]>))]
[JsonSerializable(typeof(ProcessingEndpoints.CreateProcessingStepRequest))]
[JsonSerializable(typeof(Result<Verification>))]
[JsonSerializable(typeof(Result<Verification[]>))]
[JsonSerializable(typeof(ProcessingEndpoints.CreateVerificationRequest))]
internal partial class AppJsonContext : JsonSerializerContext;
