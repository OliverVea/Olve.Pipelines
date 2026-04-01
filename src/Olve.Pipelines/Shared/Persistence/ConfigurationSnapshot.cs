using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Shared.Persistence;

public record ConfigurationSnapshot(
    PipelineData[] Pipelines,
    SourceData[] Sources,
    BuilderData[] Builders,
    ProcessingStepData[] ProcessingSteps,
    VerificationData[] Verifications);

public record PipelineData(Id<Pipeline> Id, string Name);

public record SourceData(Id<PipelineSource> Id, string Name, Id<Pipeline> PipelineId, string Type, GitHubSourceData? GitHub, HardcodedSourceData? Hardcoded);
public record GitHubSourceData(string Owner, string Repository, string Branch);
public record HardcodedSourceData(Dictionary<string, string> Files);

public record BuilderData(Id<PipelineBuilder> Id, string Name, Id<Pipeline> PipelineId, string Type, ScriptData? Script);

public record ProcessingStepData(Id<ProcessingStep> Id, string Name, Id<Pipeline> PipelineId, string Type, ScriptData? Script);

public record VerificationData(Id<Verification> Id, string Name, Id<ProcessingStep> ProcessingStepId, string Type, ScriptData? Script);

public record ScriptData(string Script);
