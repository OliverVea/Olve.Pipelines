using Olve.Pipelines.Pipelines.Processing;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Persisted operational promotion-gate state, kept separate from <see cref="ConfigurationSnapshot"/>
/// because it is state, not git-owned config. Only blocked steps are recorded (absence == enabled),
/// so an empty list is the normal, all-enabled baseline.
/// </summary>
public record PromotionSnapshot(ProcessingStepPromotionData[] BlockedSteps);

public record ProcessingStepPromotionData(Id<ProcessingStep> ProcessingStepId);
