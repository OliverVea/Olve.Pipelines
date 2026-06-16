namespace Olve.Pipelines.Pipelines.Processing;

/// <summary>
/// Operational promotion-gate state for a processing step. This is <b>state, not config</b>: it is
/// API-mutable and present even on a git-bound pipeline (configuration is git-owned, but operations
/// like blocking/unblocking promotion and re-promoting stay available). Stored as an attachment
/// keyed on the step Id; <i>absence means enabled</i>, so only blocked steps carry an attachment.
/// </summary>
public record ProcessingStepPromotion(bool Blocked);
