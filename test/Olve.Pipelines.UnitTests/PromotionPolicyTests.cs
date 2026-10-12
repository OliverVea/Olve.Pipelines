using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Processing.PromotionIntent;
using static Olve.Pipelines.Pipelines.Processing.PromotionRefusal;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// The promote / unblock decision table. Each row is one combination of facts about step B; the
/// expected outcome is either "start B with the bundle that last succeeded at the previous step"
/// (<c>null</c> refusal) or a refusal.
/// </summary>
public class PromotionPolicyTests
{
    /// <summary>What the previous step's last success is.</summary>
    public enum Src
    {
        /// <summary>Nothing has ever succeeded at the previous step.</summary>
        None,
        /// <summary>It succeeded with a bundle whose build record is gone / never completed.</summary>
        Gone,
        /// <summary>It succeeded with a bundle that has a completed build record.</summary>
        Present,
        /// <summary>Defensive: a build record is supplied, but for another bundle id.</summary>
        Mismatched,
    }

    /// <summary>What B last ran successfully, relative to the previous step's last success.</summary>
    public enum Last
    {
        Never,
        /// <summary>B's last success is the same bundle.</summary>
        Same,
        /// <summary>B's last success is an older build (so the candidate is newer).</summary>
        Older,
        /// <summary>B's last success is a newer build (so the candidate would be a downgrade).</summary>
        Newer,
        /// <summary>A different bundle built at the very same instant.</summary>
        SameInstant,
        /// <summary>B's last success has no build record.</summary>
        Unknown,
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 5, 49, 21, TimeSpan.Zero);

    [Test]
    // ---- Promote ----
    [Arguments(Promote, false, true, Src.Present, Last.Never, false, null)]
    [Arguments(Promote, false, true, Src.Present, Last.Older, false, null)]
    [Arguments(Promote, false, true, Src.Present, Last.Same, false, null)] // retry a flaky deploy: same bundle again
    [Arguments(Promote, false, true, Src.Present, Last.SameInstant, false, null)]
    [Arguments(Promote, false, true, Src.Present, Last.Newer, false, WouldDowngrade)]
    [Arguments(Promote, false, true, Src.Present, Last.Unknown, false, UnknownOrder)]
    [Arguments(Promote, false, true, Src.None, Last.Never, false, NothingPassedSource)]
    [Arguments(Promote, false, true, Src.None, Last.Older, false, NothingPassedSource)]
    [Arguments(Promote, false, true, Src.Gone, Last.Older, false, BundleGone)]
    [Arguments(Promote, false, true, Src.Gone, Last.Never, false, BundleGone)]
    [Arguments(Promote, false, true, Src.Mismatched, Last.Never, false, BundleGone)]
    [Arguments(Promote, true, true, Src.Present, Last.Older, false, Blocked)] // the brake stays a brake
    [Arguments(Promote, true, true, Src.None, Last.Never, false, Blocked)]
    [Arguments(Promote, true, false, Src.Present, Last.Older, true, Blocked)] // blocked wins over everything
    [Arguments(Promote, false, false, Src.Present, Last.Older, false, NotConfigured)]
    [Arguments(Promote, false, true, Src.Present, Last.Older, true, JobActive)]
    [Arguments(Promote, false, true, Src.Present, Last.Same, true, JobActive)]
    [Arguments(Promote, false, true, Src.Present, Last.Never, true, JobActive)]
    [Arguments(Promote, false, true, Src.Gone, Last.Older, true, BundleGone)]
    // ---- Unblock (facts are taken after the gate is cleared) ----
    [Arguments(Unblock, false, true, Src.Present, Last.Never, false, null)]
    [Arguments(Unblock, false, true, Src.Present, Last.Older, false, null)] // the staged release: held build goes through
    [Arguments(Unblock, false, true, Src.Present, Last.SameInstant, false, null)]
    [Arguments(Unblock, false, true, Src.Present, Last.Same, false, AlreadyRan)]
    [Arguments(Unblock, false, true, Src.Present, Last.Newer, false, WouldDowngrade)] // never an automatic downgrade
    [Arguments(Unblock, false, true, Src.Present, Last.Unknown, false, UnknownOrder)]
    [Arguments(Unblock, false, true, Src.None, Last.Never, false, NothingPassedSource)]
    [Arguments(Unblock, false, true, Src.None, Last.Older, false, NothingPassedSource)]
    [Arguments(Unblock, false, true, Src.Gone, Last.Older, false, BundleGone)]
    [Arguments(Unblock, false, true, Src.Present, Last.Older, true, JobActive)]
    [Arguments(Unblock, false, true, Src.Present, Last.Same, true, JobActive)] // unblock twice, first one still running
    [Arguments(Unblock, false, false, Src.Present, Last.Older, false, NotConfigured)]
    [Arguments(Unblock, true, true, Src.Present, Last.Older, false, Blocked)] // re-blocked before the check
    public async Task Decide(
        PromotionIntent intent, bool blocked, bool configured, Src src, Last last, bool active, PromotionRefusal? expected)
    {
        var candidateId = Id.New<ArtifactBundle>();
        var candidate = new PromotionBundle(candidateId, T0);

        Id<ArtifactBundle>? sourceSuccess = src == Src.None ? null : candidateId;
        PromotionBundle? sourceBuilt = src switch
        {
            Src.Present => candidate,
            Src.Mismatched => new PromotionBundle(Id.New<ArtifactBundle>(), T0),
            _ => null,
        };

        var otherId = Id.New<ArtifactBundle>();
        (Id<ArtifactBundle>? lastSuccess, PromotionBundle? lastBuilt) = last switch
        {
            Last.Never => ((Id<ArtifactBundle>?)null, (PromotionBundle?)null),
            Last.Same => (candidateId, candidate),
            Last.Older => (otherId, new PromotionBundle(otherId, T0.AddHours(-1))),
            Last.Newer => (otherId, new PromotionBundle(otherId, T0.AddHours(1))),
            Last.SameInstant => (otherId, new PromotionBundle(otherId, T0)),
            Last.Unknown => (otherId, null),
            _ => throw new ArgumentOutOfRangeException(nameof(last)),
        };

        var facts = new PromotionFacts(blocked, configured, sourceSuccess, sourceBuilt, lastSuccess, lastBuilt, active);

        var decision = PromotionPolicy.Decide(intent, facts);

        await Assert.That(decision.Refusal).IsEqualTo(expected);
        if (expected is null)
        {
            // A start always uses exactly the previous step's last success — never B's own last bundle.
            await Assert.That(decision.Starts).IsTrue();
            await Assert.That(decision.Bundle).IsEqualTo(candidate);
        }
        else
        {
            await Assert.That(decision.Starts).IsFalse();
            await Assert.That(decision.Bundle).IsNull();
        }
    }

    /// <summary>B's most recent run that actually started (it may have failed), relative to the candidate.</summary>
    public enum Run { None, SameAsCandidate, Older, Newer, Unknown }

    // The no-downgrade rule also covers B's latest started attempt: a failed deploy of a newer build
    // may have half-applied, so the previous step's (older) last success must not follow it.
    [Test]
    [Arguments(Promote, Last.Older, Run.Newer, WouldDowngrade)]
    [Arguments(Unblock, Last.Older, Run.Newer, WouldDowngrade)]
    [Arguments(Promote, Last.Same, Run.Newer, WouldDowngrade)] // same bundle as last success, but a newer attempt since
    [Arguments(Unblock, Last.Never, Run.Newer, WouldDowngrade)]
    [Arguments(Promote, Last.Older, Run.SameAsCandidate, null)] // retry: the failed attempt was this very bundle
    [Arguments(Unblock, Last.Older, Run.SameAsCandidate, null)]
    [Arguments(Promote, Last.Never, Run.SameAsCandidate, null)]
    [Arguments(Promote, Last.Older, Run.Older, null)]
    [Arguments(Unblock, Last.Same, Run.Older, AlreadyRan)]
    [Arguments(Promote, Last.Older, Run.Unknown, UnknownOrder)]
    [Arguments(Unblock, Last.Older, Run.Unknown, UnknownOrder)]
    [Arguments(Promote, Last.Older, Run.None, null)]
    public async Task Decide_ConsidersLatestStartedRun(PromotionIntent intent, Last last, Run run, PromotionRefusal? expected)
    {
        var candidateId = Id.New<ArtifactBundle>();
        var candidate = new PromotionBundle(candidateId, T0);

        var lastId = Id.New<ArtifactBundle>();
        (Id<ArtifactBundle>? lastSuccess, PromotionBundle? lastBuilt) = last switch
        {
            Last.Never => ((Id<ArtifactBundle>?)null, (PromotionBundle?)null),
            Last.Same => (candidateId, candidate),
            Last.Older => (lastId, new PromotionBundle(lastId, T0.AddHours(-1))),
            _ => throw new ArgumentOutOfRangeException(nameof(last)),
        };

        var runId = Id.New<ArtifactBundle>();
        (Id<ArtifactBundle>? lastRun, PromotionBundle? runBuilt) = run switch
        {
            Run.None => ((Id<ArtifactBundle>?)null, (PromotionBundle?)null),
            Run.SameAsCandidate => (candidateId, candidate),
            Run.Older => (runId, new PromotionBundle(runId, T0.AddHours(-2))),
            Run.Newer => (runId, new PromotionBundle(runId, T0.AddHours(2))),
            Run.Unknown => (runId, null),
            _ => throw new ArgumentOutOfRangeException(nameof(run)),
        };

        var facts = new PromotionFacts(false, true, candidateId, candidate, lastSuccess, lastBuilt, false, lastRun, runBuilt);

        var decision = PromotionPolicy.Decide(intent, facts);

        await Assert.That(decision.Refusal).IsEqualTo(expected);
        await Assert.That(decision.Bundle).IsEqualTo(expected is null ? candidate : null);
    }
}
