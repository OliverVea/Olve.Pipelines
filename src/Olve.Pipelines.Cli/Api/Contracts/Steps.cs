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

/// <summary>A bundle as the promotion logic names it: id + when its production run was created.</summary>
public sealed class PromotionBundle
{
    public Guid ArtifactBundleId { get; set; }
    public DateTimeOffset BuiltAt { get; set; }
}

/// <summary>Where a step's promotions come from: the previous step, or production (<c>ProcessingStepId</c> null).</summary>
public sealed class PromotionSource
{
    public Guid? ProcessingStepId { get; set; }
    public string Name { get; set; } = "";
    public bool IsProduction { get; set; }
}

/// <summary>What an action would do now: run with <see cref="Bundle"/>, or not, because <see cref="Reason"/>.</summary>
public sealed class PromotionOption
{
    public bool Allowed { get; set; }
    public PromotionBundle? Bundle { get; set; }
    public string? Reason { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// A processing step's promotion gate plus what Promote (and unblocking, when blocked) would use now.
/// Returned per step and in the pipeline-wide listing.
/// </summary>
public sealed class StepPromotionState
{
    public Guid ProcessingStepId { get; set; }
    public string StepName { get; set; } = "";
    public bool Blocked { get; set; }
    public PromotionSource Source { get; set; } = new();
    public PromotionBundle? SourceLastSuccess { get; set; }
    public PromotionBundle? LastSuccess { get; set; }
    public PromotionOption Promote { get; set; } = new();
    public PromotionOption? OnUnblock { get; set; }
}

/// <summary>The promotion check an unblock ran.</summary>
public sealed class UnblockPromotion
{
    public bool Started { get; set; }
    public Guid? JobGroupId { get; set; }
    public PromotionBundle? Bundle { get; set; }
    public string? Reason { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Response of <c>PUT /api/processing-steps/{stepId}/promotion</c>.</summary>
public sealed class PromotionState
{
    public bool Blocked { get; set; }
    public UnblockPromotion? Promotion { get; set; }
}

/// <summary>Response of <c>POST /api/processing-steps/{stepId}/promote</c>.</summary>
public sealed class PromoteResult
{
    public Guid JobGroupId { get; set; }
    public Guid ProcessingStepId { get; set; }
    public PromotionBundle Bundle { get; set; } = new();
    public PromotionSource Source { get; set; } = new();
    public string Message { get; set; } = "";
}
