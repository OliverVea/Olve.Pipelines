using System.Text.Json.Serialization;
using Olve.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines;

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(ResultProblem[]))]
[JsonSerializable(typeof(Result<Pipeline>))]
[JsonSerializable(typeof(Result<Pipeline[]>))]
[JsonSerializable(typeof(Result<ProductionStep>))]
[JsonSerializable(typeof(Result<ProductionStep[]>))]
[JsonSerializable(typeof(ProductionStepEndpoints.CreateProductionStepRequest))]
[JsonSerializable(typeof(ProductionStepEndpoints.SetStepConfigurationRequest), TypeInfoPropertyName = "ProductionSetStepConfigurationRequest")]
[JsonSerializable(typeof(Result<StepConfiguration>))]
[JsonSerializable(typeof(Result<ProcessingStep>))]
[JsonSerializable(typeof(Result<ProcessingStep[]>))]
[JsonSerializable(typeof(ProcessingStepEndpoints.CreateProcessingStepRequest))]
[JsonSerializable(typeof(ProcessingStepEndpoints.SetStepConfigurationRequest), TypeInfoPropertyName = "ProcessingSetStepConfigurationRequest")]
[JsonSerializable(typeof(ProcessingStepEndpoints.UpdateOrderRequest))]
[JsonSerializable(typeof(Result<ArtifactBundle>))]
[JsonSerializable(typeof(Result<ArtifactBundle[]>))]
[JsonSerializable(typeof(Result<string>))]
[JsonSerializable(typeof(Result<string[]>))]
[JsonSerializable(typeof(Result<Job>))]
[JsonSerializable(typeof(Result<Job[]>))]
[JsonSerializable(typeof(Result<Id<Job>[]>))]
[JsonSerializable(typeof(Job.ProductionJob))]
[JsonSerializable(typeof(Job.ProcessingJob))]
[JsonSerializable(typeof(DeletionResult))]
[JsonSerializable(typeof(SetSecretRequest))]
internal partial class AppJsonContext : JsonSerializerContext;
