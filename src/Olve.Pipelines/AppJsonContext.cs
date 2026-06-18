using System.Text.Json.Serialization;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
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
[JsonSerializable(typeof(Result<ProductionStep>))]
[JsonSerializable(typeof(Result<ProductionStep[]>))]
[JsonSerializable(typeof(Result<StepConfiguration>))]
[JsonSerializable(typeof(Result<ProcessingStep>))]
[JsonSerializable(typeof(Result<ProcessingStep[]>))]
[JsonSerializable(typeof(ProcessingStepEndpoints.SetPromotionRequest))]
[JsonSerializable(typeof(Result<ProcessingStepEndpoints.PromotionState>))]
[JsonSerializable(typeof(Result<ProcessingStepEndpoints.StepPromotionState[]>))]
[JsonSerializable(typeof(Result<ArtifactBundle>))]
[JsonSerializable(typeof(Result<ArtifactBundle[]>))]
[JsonSerializable(typeof(Result<string>))]
[JsonSerializable(typeof(Result<string[]>))]
[JsonSerializable(typeof(Result<Job>))]
[JsonSerializable(typeof(Result<Job[]>))]
[JsonSerializable(typeof(Result<Page<Job>>))]
[JsonSerializable(typeof(JobSortField))]
[JsonSerializable(typeof(Result<Id<Job>[]>))]
[JsonSerializable(typeof(Job.ProductionJob))]
[JsonSerializable(typeof(Job.ProcessingJob))]
[JsonSerializable(typeof(Result<JobGroup>))]
[JsonSerializable(typeof(ProductionJobGroup))]
[JsonSerializable(typeof(ProcessingJobGroup))]
[JsonSerializable(typeof(Result<Trigger>))]
[JsonSerializable(typeof(Result<Trigger[]>))]
[JsonSerializable(typeof(TriggerEndpoints.WebhookRequest))]
[JsonSerializable(typeof(ProductionTriggerTarget))]
[JsonSerializable(typeof(ProcessingTriggerTarget))]
[JsonSerializable(typeof(PollTriggerTarget))]
[JsonSerializable(typeof(PipelineDocument))]
[JsonSerializable(typeof(Result<PipelineDocument>))]
[JsonSerializable(typeof(PipelineConfigBinding))]
[JsonSerializable(typeof(Result<PipelineConfigBinding>))]
[JsonSerializable(typeof(CreatePipelineWithRepoRequest))]
[JsonSerializable(typeof(UpdateBindingRequest))]
[JsonSerializable(typeof(Result<PipelineBindingStatus>))]
[JsonSerializable(typeof(ProductionTargetDocument))]
[JsonSerializable(typeof(ProcessingTargetDocument))]
[JsonSerializable(typeof(PollTargetDocument))]
[JsonSerializable(typeof(DeletionResult))]
[JsonSerializable(typeof(SetSecretRequest))]
[JsonSerializable(typeof(TriggerProcessingRequest))]
[JsonSerializable(typeof(FrontendConfigEndpoints.FrontendConfig))]
internal partial class AppJsonContext : JsonSerializerContext;
