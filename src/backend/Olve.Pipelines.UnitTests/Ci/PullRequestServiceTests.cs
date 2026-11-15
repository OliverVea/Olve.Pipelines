using Olve.Pipelines.GitHub;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;
using static Olve.Pipelines.UnitTests.Ci.PullRequestFixtures;

namespace Olve.Pipelines.UnitTests.Ci;

public class PullRequestServiceTests
{
    private static readonly DateTimeOffset Seen = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
    private static readonly DateTimeOffset Later = Seen.AddMinutes(5);

    private static (PullRequestService Service, Id<Pipeline> PipelineId) Create() =>
        (new PullRequestService(new EntityStore<PullRequest>([]), new IdProvider()), Id.New<Pipeline>());

    private static readonly Dictionary<string, (Func<GitHubPullRequest> Reported, PullRequestChange Change, PullRequestState? State)> FirstReports = new()
    {
        ["an open pull request into the branch is tracked"] = (() => Pull(), PullRequestChange.Tracked, PullRequestState.Open),
        ["a merged one is tracked as merged"] = (() => Pull(state: "closed", merged: true), PullRequestChange.Tracked, PullRequestState.Merged),
        ["a closed one is tracked as closed"] = (() => Pull(state: "closed"), PullRequestChange.Tracked, PullRequestState.Closed),
        ["a pull request from a fork is ignored"] = (() => Pull(headRepo: "someone/Olve.Pipelines"), PullRequestChange.Ignored, null),
        ["one whose fork was deleted is ignored"] = (() => Pull(headRepo: null), PullRequestChange.Ignored, null),
        ["one into another branch is ignored"] = (() => Pull(@base: "release"), PullRequestChange.Ignored, null),
    };

    public static IEnumerable<string> FirstReportRules() => FirstReports.Keys;

    [Test]
    [MethodDataSource(nameof(FirstReportRules))]
    public async Task A_first_report(string rule)
    {
        var (pulls, pipelineId) = Create();
        var (reported, change, state) = FirstReports[rule];
        var pull = reported();

        await Assert.That(pulls.Observe(pipelineId, "main", pull, Seen)).IsEqualTo(change);
        await Assert.That(pulls.TryGet(pipelineId, pull.Number, out var held) ? held.State : (PullRequestState?)null).IsEqualTo(state);
    }

    [Test]
    public async Task A_tracked_pull_request_keeps_what_GitHub_reports()
    {
        var (pulls, pipelineId) = Create();

        pulls.Observe(pipelineId, "main", Pull(number: 12, draft: true, mergeable: false, title: "Draft it"), Seen);

        await Assert.That(pulls.TryGet(pipelineId, 12, out var held)).IsTrue();
        await Assert.That((held!.PipelineId, held.Title, held.Branch, held.HeadSha, held.Draft, held.State, held.Mergeable, held.UpdatedAt, held.LastPushAt))
            .IsEqualTo((pipelineId, "Draft it", "feature-12", "aaaaaaa", true, PullRequestState.Open, (bool?)false, DateTimeOffset.Parse("2026-11-15T10:00:00Z"), Seen));
    }

    [Test]
    public async Task A_new_head_is_a_push_and_forgets_mergeability_while_the_same_head_keeps_both()
    {
        var (pulls, pipelineId) = Create();
        var t0 = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        pulls.Observe(pipelineId, "main", Pull(mergeable: true, updatedAt: t0), Seen);

        pulls.Observe(pipelineId, "main", Pull(title: "Renamed", mergeable: null, updatedAt: t0.AddMinutes(1)), Later);
        pulls.TryGet(pipelineId, 7, out var retitled);
        pulls.Observe(pipelineId, "main", Pull(headSha: "bbbbbbb", mergeable: null, updatedAt: t0.AddMinutes(2)), Later);
        pulls.TryGet(pipelineId, 7, out var pushed);

        await Assert.That((retitled!.Title, retitled.Mergeable, retitled.LastPushAt)).IsEqualTo(("Renamed", (bool?)true, Seen));
        await Assert.That((pushed!.HeadSha, pushed.Mergeable, pushed.LastPushAt)).IsEqualTo(("bbbbbbb", (bool?)null, Later));
    }

    [Test]
    public async Task A_report_older_than_the_one_held_is_ignored()
    {
        var (pulls, pipelineId) = Create();
        var t0 = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        pulls.Observe(pipelineId, "main", Pull(state: "closed", updatedAt: t0), Seen);

        var change = pulls.Observe(pipelineId, "main", Pull(state: "open", updatedAt: t0.AddSeconds(-1)), Later);

        await Assert.That(change).IsEqualTo(PullRequestChange.Ignored);
        await Assert.That(pulls.TryGet(pipelineId, 7, out var held) ? held.State : default).IsEqualTo(PullRequestState.Closed);
    }

    [Test]
    public async Task A_pull_request_retargeted_away_from_the_branch_is_no_longer_tracked()
    {
        var (pulls, pipelineId) = Create();
        var t0 = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        pulls.Observe(pipelineId, "main", Pull(updatedAt: t0), Seen);

        var change = pulls.Observe(pipelineId, "main", Pull(@base: "release", updatedAt: t0.AddMinutes(1)), Later);

        await Assert.That(change).IsEqualTo(PullRequestChange.Removed);
        await Assert.That(pulls.TryGet(pipelineId, 7, out _)).IsFalse();
    }

    [Test]
    public async Task Pipelines_track_their_own_pull_requests()
    {
        var (pulls, first) = Create();
        var second = Id.New<Pipeline>();

        pulls.Observe(first, "main", Pull(number: 1), Seen);
        pulls.Observe(second, "main", Pull(number: 1, title: "Other"), Seen);

        await Assert.That(pulls.ListByPipeline(first).Single().Title).IsEqualTo("Add a thing");
        await Assert.That(pulls.ListByPipeline(second).Single().Title).IsEqualTo("Other");
    }
}
