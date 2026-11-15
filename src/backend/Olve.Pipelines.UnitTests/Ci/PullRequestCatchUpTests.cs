using Olve.Pipelines.GitHub;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;
using static Olve.Pipelines.UnitTests.Ci.PullRequestFixtures;

namespace Olve.Pipelines.UnitTests.Ci;

public class PullRequestCatchUpTests
{
    private sealed class FakeSource : IPullRequestSource
    {
        public Result<IReadOnlyList<GitHubPullRequest>> Open { get; set; } = Result.Success<IReadOnlyList<GitHubPullRequest>>([]);
        public Dictionary<int, GitHubPullRequest?> Single { get; } = [];
        public List<int> Read { get; } = [];

        public Task<Result<IReadOnlyList<GitHubPullRequest>>> ListOpenAsync(PipelineConfigBinding binding, CancellationToken ct = default)
            => Task.FromResult(Open);

        public Task<Result<GitHubPullRequest?>> GetAsync(PipelineConfigBinding binding, int number, CancellationToken ct = default)
        {
            Read.Add(number);
            return Task.FromResult(Result.Success(Single.GetValueOrDefault(number)));
        }

        public Task<Result<GitHubGitCommit>> GetCommitAsync(PipelineConfigBinding binding, string sha, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<string>>> ChangedFilesAsync(PipelineConfigBinding binding, string @base, string head, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T10:00:00Z");

    private static PipelineConfigBinding Binding() => new(
        Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), Repo, "main", ".pipelines", null, null, null,
        ReconcileStatus.NeverRun, DateTimeOffset.UnixEpoch);

    private static (PullRequestCatchUp CatchUp, PullRequestService Pulls, FakeSource Source) Create()
    {
        var pulls = new PullRequestService(new EntityStore<PullRequest>([]), new IdProvider(), PullRequestFixtures.AllowedAuthors);
        var source = new FakeSource();
        return (new PullRequestCatchUp(source, pulls, new FixedTime(T0.AddHours(1))), pulls, source);
    }

    private static PullRequestState? StateOf(PullRequestService pulls, PipelineConfigBinding binding, int number)
        => pulls.TryGet(binding.PipelineId, number, out var held) ? held.State : null;

    [Test]
    public async Task Open_pull_requests_GitHub_lists_are_tracked()
    {
        var (catchUp, pulls, source) = Create();
        var binding = Binding();
        source.Open = Result.Success<IReadOnlyList<GitHubPullRequest>>([Pull(number: 1), Pull(number: 2, draft: true)]);

        await Assert.That(await catchUp.CatchUpAsync(binding, default)).Succeeded();

        await Assert.That(pulls.ListByPipeline(binding.PipelineId).Select(p => (p.Number, p.Draft)).Order()).IsEquivalentTo([(1, false), (2, true)]);
        await Assert.That(source.Read).IsEmpty();
    }

    [Test]
    public async Task One_held_open_that_GitHub_no_longer_lists_is_read_on_its_own()
    {
        var (catchUp, pulls, source) = Create();
        var binding = Binding();
        foreach (var n in new[] { 1, 2, 3, 4 })
            pulls.Observe(binding.PipelineId, "main", Pull(number: n, updatedAt: T0), T0);
        source.Open = Result.Success<IReadOnlyList<GitHubPullRequest>>([Pull(number: 1, updatedAt: T0)]);
        source.Single[2] = Pull(number: 2, state: "closed", merged: true, updatedAt: T0.AddMinutes(1));
        source.Single[3] = Pull(number: 3, @base: "release", updatedAt: T0.AddMinutes(1));
        source.Single[4] = null;

        await Assert.That(await catchUp.CatchUpAsync(binding, default)).Succeeded();

        await Assert.That(source.Read.Order()).IsEquivalentTo([2, 3, 4]);
        await Assert.That(StateOf(pulls, binding, 1)).IsEqualTo(PullRequestState.Open);
        await Assert.That(StateOf(pulls, binding, 2)).IsEqualTo(PullRequestState.Merged);
        await Assert.That(StateOf(pulls, binding, 3)).IsNull();
        await Assert.That(StateOf(pulls, binding, 4)).IsNull();
    }

    [Test]
    public async Task A_listing_GitHub_refuses_changes_nothing()
    {
        var (catchUp, pulls, source) = Create();
        var binding = Binding();
        pulls.Observe(binding.PipelineId, "main", Pull(number: 1), T0);
        source.Open = new ResultProblem("GitHub returned 403 listing the open pull requests of 'OliverVea/Olve.Pipelines' into 'main'.");

        var result = await catchUp.CatchUpAsync(binding, default);

        await result.Problems.IsSingle<ResultProblem>("GitHub returned 403 listing the open pull requests of 'OliverVea/Olve.Pipelines' into 'main'.");
        await Assert.That(StateOf(pulls, binding, 1)).IsEqualTo(PullRequestState.Open);
        await Assert.That(source.Read).IsEmpty();
    }
}
