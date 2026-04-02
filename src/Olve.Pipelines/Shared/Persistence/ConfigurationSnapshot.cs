using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;

namespace Olve.Pipelines.Shared.Persistence;

public record ConfigurationSnapshot(
    PipelineData[] Pipelines,
    ProductionStepData[] ProductionSteps,
    ProcessingStepData[] ProcessingSteps);

public record PipelineData(Id<Pipeline> Id, string Name);

public record StepConfigurationData(string Image, string Script, Dictionary<string, string>? EnvironmentVariables);

public record ProductionStepData(Id<ProductionStep> Id, string Name, Id<Pipeline> PipelineId, StepConfigurationData? Configuration);

public record ProcessingStepData(Id<ProcessingStep> Id, string Name, Id<Pipeline> PipelineId, int Order, StepConfigurationData? Configuration);
