using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

/// <summary>
/// Reads and mutates the per-step promotion gate (see <see cref="ProcessingStepPromotion"/>). The
/// gate is operational state, kept in an <see cref="AttachmentStore{ProcessingStep,T}"/> keyed on the
/// step Id so it survives a name-preserving reconcile but is auto-cleaned when the step is deleted.
///
/// <see cref="IsBlocked"/> is the short-circuit consulted by every path that creates a processing
/// job (the automatic promotion cascade and the manual/trigger paths) — analogous to the reconcile
/// pause check, but per step. Absence of an attachment means enabled.
/// </summary>
public class PromotionGateService(AttachmentStore<ProcessingStep, ProcessingStepPromotion> store)
{
    /// <summary>True when promotion INTO this step is blocked. Absence of state means enabled.</summary>
    public bool IsBlocked(Id<ProcessingStep> stepId)
        => store.TryGet(stepId, out var promotion) && promotion.Blocked;

    /// <summary>Blocks promotion into the step (idempotent).</summary>
    public void Block(Id<ProcessingStep> stepId) => store.Set(stepId, new ProcessingStepPromotion(true));

    /// <summary>Unblocks promotion into the step (idempotent). Clears state to keep absence == enabled.</summary>
    public void Unblock(Id<ProcessingStep> stepId) => store.Remove(stepId);

    /// <summary>Sets the gate to <paramref name="blocked"/>.</summary>
    public void Set(Id<ProcessingStep> stepId, bool blocked)
    {
        if (blocked)
            Block(stepId);
        else
            Unblock(stepId);
    }
}
