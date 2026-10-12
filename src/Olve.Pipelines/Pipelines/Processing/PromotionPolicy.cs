using Olve.Pipelines.Pipelines.Building;

namespace Olve.Pipelines.Pipelines.Processing;

/// <summary>
/// A bundle as the promotion logic sees it: its id and when its production run was created
/// (<see cref="BuiltAt"/>, the order "newer/older" is judged by). Derived from the persisted
/// production job group, not the in-memory <see cref="ArtifactBundle"/> record (which a restart wipes).
/// </summary>
public record PromotionBundle(Id<ArtifactBundle> ArtifactBundleId, DateTimeOffset BuiltAt);

/// <summary>Why a promotion is not started. Wire names are the camelCased member names.</summary>
public enum PromotionRefusal
{
    /// <summary>The step's promotion gate is blocked (the brake).</summary>
    Blocked,
    /// <summary>The step has no configuration to run.</summary>
    NotConfigured,
    /// <summary>Nothing has ever succeeded at the previous step (or no production run has completed).</summary>
    NothingPassedSource,
    /// <summary>The previous step's last success refers to a bundle whose build record is gone or never completed.</summary>
    BundleGone,
    /// <summary>A job for this step is already scheduled or in progress.</summary>
    JobActive,
    /// <summary>Unblock only: the step's last success already is the previous step's last success.</summary>
    AlreadyRan,
    /// <summary>The previous step's last success is older than what this step last ran (its last success, or its latest attempt that started).</summary>
    WouldDowngrade,
    /// <summary>What this step last ran has no build record, so "older" cannot be judged.</summary>
    UnknownOrder,
}

/// <summary>What is being decided: an explicit Promote click, or the check that runs when a step is unblocked.</summary>
public enum PromotionIntent { Promote, Unblock }

/// <summary>
/// Everything the decision needs about one processing step "B", gathered by <see cref="PromotionService"/>.
/// </summary>
/// <param name="Blocked">B's promotion gate.</param>
/// <param name="Configured">B has a step configuration.</param>
/// <param name="SourceSuccess">The bundle that most recently succeeded at the step before B (for the first
/// step: the most recently completed production run). Null when nothing has.</param>
/// <param name="SourceSuccessBuilt">The build record of <paramref name="SourceSuccess"/>; null when it is gone
/// or its production run never completed.</param>
/// <param name="LastSuccess">The bundle B most recently ran successfully; null when B never succeeded.</param>
/// <param name="LastSuccessBuilt">The build record of <paramref name="LastSuccess"/>; null when unknown.</param>
/// <param name="HasActiveJob">A job for B is Scheduled or InProgress.</param>
/// <param name="LastRun">The bundle of B's most recent run that actually started (Done, Failed, or cancelled
/// after starting) — it may have changed the target even if it failed. Null when B never started a run.</param>
/// <param name="LastRunBuilt">The build record of <paramref name="LastRun"/>; null when unknown.</param>
public record PromotionFacts(
    bool Blocked,
    bool Configured,
    Id<ArtifactBundle>? SourceSuccess,
    PromotionBundle? SourceSuccessBuilt,
    Id<ArtifactBundle>? LastSuccess,
    PromotionBundle? LastSuccessBuilt,
    bool HasActiveJob,
    Id<ArtifactBundle>? LastRun = null,
    PromotionBundle? LastRunBuilt = null);

/// <summary>The outcome: start B with <see cref="Bundle"/>, or refuse with <see cref="Refusal"/>.</summary>
public record PromotionDecision(PromotionBundle? Bundle, PromotionRefusal? Refusal)
{
    public bool Starts => Bundle is not null;

    public static PromotionDecision Start(PromotionBundle bundle) => new(bundle, null);
    public static PromotionDecision Refuse(PromotionRefusal refusal) => new(null, refusal);
}

/// <summary>
/// The single promote/unblock rule. Pure: the table of unit tests in <c>PromotionPolicyTests</c> pins
/// every row. Promote and the unblock check differ in one place only: when the step already ran the
/// previous step's last success, Promote re-runs it (the "retry a flaky deploy" case) while unblock
/// has nothing to do. Neither ever goes backwards — not behind B's last success, and not behind B's
/// latest started attempt (a failed deploy may have half-applied) — and neither falls back to an older
/// success when the newest one is gone.
/// </summary>
public static class PromotionPolicy
{
    public static PromotionDecision Decide(PromotionIntent intent, PromotionFacts facts)
    {
        if (facts.Blocked)
            return PromotionDecision.Refuse(PromotionRefusal.Blocked);

        if (!facts.Configured)
            return PromotionDecision.Refuse(PromotionRefusal.NotConfigured);

        if (facts.SourceSuccess is not { } source)
            return PromotionDecision.Refuse(PromotionRefusal.NothingPassedSource);

        if (facts.SourceSuccessBuilt is not { } candidate || candidate.ArtifactBundleId != source)
            return PromotionDecision.Refuse(PromotionRefusal.BundleGone);

        if (facts.HasActiveJob)
            return PromotionDecision.Refuse(PromotionRefusal.JobActive);

        // Never older than anything B last ran.
        foreach (var (ran, built) in new[] { (facts.LastSuccess, facts.LastSuccessBuilt), (facts.LastRun, facts.LastRunBuilt) })
        {
            if (ran is not { } ranId || ranId == candidate.ArtifactBundleId)
                continue;

            if (built is not { } ranBuilt || ranBuilt.ArtifactBundleId != ranId)
                return PromotionDecision.Refuse(PromotionRefusal.UnknownOrder);

            if (candidate.BuiltAt < ranBuilt.BuiltAt)
                return PromotionDecision.Refuse(PromotionRefusal.WouldDowngrade);
        }

        if (facts.LastSuccess == candidate.ArtifactBundleId && intent == PromotionIntent.Unblock)
            return PromotionDecision.Refuse(PromotionRefusal.AlreadyRan);

        return PromotionDecision.Start(candidate);
    }
}
