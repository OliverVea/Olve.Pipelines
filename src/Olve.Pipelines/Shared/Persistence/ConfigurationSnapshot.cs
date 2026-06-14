using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.Shared.Persistence;

public record ConfigurationSnapshot(
    PipelineData[] Pipelines,
    ProductionStepData[] ProductionSteps,
    ProcessingStepData[] ProcessingSteps,
    TriggerData[]? Triggers = null,
    PipelineConfigBindingData[]? Bindings = null);

public record PipelineData(Id<Pipeline> Id, string Name);

public record StepConfigurationData(string Image, string Script, Dictionary<string, string>? EnvironmentVariables);

public record ProductionStepData(Id<ProductionStep> Id, string Name, Id<Pipeline> PipelineId, StepConfigurationData? Configuration);

public record ProcessingStepData(Id<ProcessingStep> Id, string Name, Id<Pipeline> PipelineId, int Order, StepConfigurationData? Configuration);

public record TriggerData(Id<Trigger> Id, Id<Pipeline> PipelineId, string Name, TriggerTarget Target, string Secret, DateTimeOffset CreatedAt);

public record PipelineConfigBindingData(Id<PipelineConfigBinding> Id, Id<Pipeline> PipelineId, DateTimeOffset CreatedAt);
