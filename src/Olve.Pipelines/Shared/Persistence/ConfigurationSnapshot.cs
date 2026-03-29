namespace Olve.Pipelines.Shared.Persistence;

public record ConfigurationSnapshot(
    PipelineData[] Pipelines,
    SourceData[] Sources,
    BuilderData[] Builders,
    ProcessingStepData[] ProcessingSteps,
    VerificationData[] Verifications);

public record PipelineData(Guid Id, string Name);

public record SourceData(Guid Id, string Name, Guid PipelineId, string Type, GitHubSourceData? GitHub, HardcodedSourceData? Hardcoded);
public record GitHubSourceData(string Owner, string Repository, string Branch);
public record HardcodedSourceData(Dictionary<string, string> Files);

public record BuilderData(Guid Id, string Name, Guid PipelineId, string Type, ScriptData? Script);

public record ProcessingStepData(Guid Id, string Name, Guid PipelineId, string Type, ScriptData? Script);

public record VerificationData(Guid Id, string Name, Guid ProcessingStepId, string Type, ScriptData? Script);

public record ScriptData(string Script);
