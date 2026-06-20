namespace Olve.Pipelines.Cli.Api.Contracts;

public sealed class ProductionStep
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid PipelineId { get; set; }
}

public sealed class ProcessingStep
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid PipelineId { get; set; }
    public int Order { get; set; }
}

public sealed class StepConfiguration
{
    public string Image { get; set; } = "";
    public string Script { get; set; } = "";
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

/// <summary>A processing step's promotion-gate state (absence of a gate = enabled).</summary>
public sealed class PromotionState
{
    public bool Blocked { get; set; }
}

/// <summary>Promotion state for one step within a pipeline-wide listing.</summary>
public sealed class StepPromotionState
{
    public Guid ProcessingStepId { get; set; }
    public bool Blocked { get; set; }
}
