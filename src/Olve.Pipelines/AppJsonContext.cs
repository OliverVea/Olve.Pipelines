using System.Text.Json.Serialization;
using Olve.Pipelines.PipelineArtifacts;
using Olve.Pipelines.PipelineArtifacts.Api;
using Olve.Pipelines.PipelineBuilds;
using Olve.Pipelines.PipelineBuilds.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.PipelineSources.Api;
using Olve.Results;

namespace Olve.Pipelines;

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(ResultProblem[]))]
[JsonSerializable(typeof(Result<Pipeline>))]
[JsonSerializable(typeof(Result<Pipeline[]>))]
[JsonSerializable(typeof(Result<PipelineSource>))]
[JsonSerializable(typeof(Result<PipelineSource[]>))]
[JsonSerializable(typeof(PipelineSource))]
[JsonSerializable(typeof(GitHubRepositorySource))]
[JsonSerializable(typeof(PipelineSourceEndpoints.SetPipelineSourceRequest))]
[JsonSerializable(typeof(PipelineSourceEndpoints.SetGitHubSourceRequest))]
[JsonSerializable(typeof(Result<PipelineBuild>))]
[JsonSerializable(typeof(Result<PipelineBuild[]>))]
[JsonSerializable(typeof(PipelineBuildEndpoints.CreatePipelineBuildRequest))]
[JsonSerializable(typeof(Result<PipelineArtifact>))]
[JsonSerializable(typeof(Result<PipelineArtifact[]>))]
[JsonSerializable(typeof(PipelineArtifactEndpoints.CreatePipelineArtifactRequest))]
[JsonSerializable(typeof(Result<PipelineProcessing.PipelineProcessingStep>))]
[JsonSerializable(typeof(Result<PipelineProcessing.PipelineProcessingStep[]>))]
[JsonSerializable(typeof(PipelineProcessing.Api.PipelineProcessingEndpoints.CreatePipelineProcessingRequest))]
[JsonSerializable(typeof(Result<PipelineProcessing.PipelineVerification>))]
[JsonSerializable(typeof(Result<PipelineProcessing.PipelineVerification[]>))]
[JsonSerializable(typeof(PipelineProcessing.Api.PipelineProcessingEndpoints.CreatePipelineVerificationRequest))]
internal partial class AppJsonContext : JsonSerializerContext;
