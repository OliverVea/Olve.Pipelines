using Microsoft.AspNetCore.Http;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Results;
using Olve.Results.TUnit;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>
/// The merge queue: what may enter it, how a batch is tested on the branch head and landed, and every way landing stops
/// short of merging something that wasn't tested as it lands.
/// </summary>
public class CiQueueTests
{
    private const string A = "a000000000000000000000000000000000000000";
    private const string B = "b000000000000000000000000000000000000000";
    private const string C = "c000000000000000000000000000000000000000";
    private const string D = "d000000000000000000000000000000000000000";

    private static CiQueueEntry Enqueue(CiScenario s, int pull) =>
        TestRunDomain.Pick(s.QueueActions.Enqueue(s.Pipeline.Id, pull, CiQueueVia.Api, "oliver"));

    public static IEnumerable<Func<(string Case, string Problem)>> Refusals()
    {
        yield return () => ("draft", "Pull request #7 is a draft.");
        yield return () => ("conflicting", "Pull request #7 doesn't merge onto 'main'.");
        yield return () => ("closed", "Pull request #7 is closed.");
        yield return () => ("already queued", "Pull request #7 is already in the merge queue.");
        yield return () => ("untracked", "Pull request #7 not found in pipeline '{0}'.");
    }

    [Test]
    [MethodDataSource(nameof(Refusals))]
    public async Task Enqueueing_is_refused_with_the_reason(string @case, string problem)
    {
        await using var s = new CiScenario();
        switch (@case)
        {
            case "draft":
                s.Push(7, A, draft: true);
                break;
            case "conflicting":
                s.Get<PullRequestService>().Observe(s.Pipeline.Id, "main", PullRequestFixtures.Pull(7, headSha: A, mergeable: false), s.Clock.GetUtcNow());
                break;
            case "closed":
                s.Push(7, A);
                s.Close(7, A, merged: false);
                break;
            case "already queued":
                s.Push(7, A);
                Enqueue(s, 7);
                break;
        }

        var result = s.QueueActions.Enqueue(s.Pipeline.Id, 7, CiQueueVia.Api, "oliver");

        await Assert.That(result).Failed();
        await Assert.That(result.Problems!.Single().FormattedMessage).IsEqualTo(string.Format(problem, s.Pipeline.Id));
    }

    [Test]
    public async Task A_pipeline_without_CI_refuses_the_queue()
    {
        await using var s = new CiScenario(m => m.Remove("ci"));
        s.Push(7, A);

        var result = s.QueueActions.Enqueue(s.Pipeline.Id, 7, CiQueueVia.Api, "oliver");

        await result.Problems.IsSingle<ConflictProblem>("Pipeline 'widgets' declares no CI.");
    }

    // The service tracks none of them, so the handler asks GitHub before refusing them by name.
    [Test]
    [Arguments("someone/fork", "main", "Pull request #9 comes from a fork.")]
    [Arguments("OliverVea/Olve.Pipelines", "release", "Pull request #9 is not into 'main'.")]
    [Arguments("OliverVea/Olve.Pipelines", "main", "Pull request #9 is by 'someone', whose pull requests get no CI.")]
    public async Task A_pull_request_from_a_fork_into_another_branch_or_by_an_author_without_CI_is_refused_by_name(string headRepo, string @base, string problem)
    {
        await using var s = new CiScenario();
        var author = problem.Contains("someone", StringComparison.Ordinal) ? "someone" : PullRequestFixtures.Author;
        s.GitHub.Answer(PullRequestFixtures.Pull(9, headSha: A, headRepo: headRepo, @base: @base, author: author));

        var response = await Handler(s).HandleAsync(
            new Contract.CiQueueEnqueueRequest(s.Pipeline.Id.Value.Value, new Contract.EnqueuePull { Pull = 9 }), default);

        var conflict = response as Contract.CiQueueEnqueueResponse.Conflict;
        await Assert.That(conflict).IsNotNull();
        await Assert.That(conflict!.Body.Detail).IsEqualTo(problem);
    }

    [Test]
    public async Task A_pull_request_that_passes_is_merged_at_the_head_it_was_tested_with_its_status_set_first()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        Enqueue(s, 7);

        await s.ProcessQueueAsync();
        var run = s.QueueRun();
        await Assert.That(run.Branch).IsEqualTo($"olve-queue/{run.Number}");
        await Assert.That(s.QueueGitHub.Created).IsEquivalentTo([run.Branch]);
        await Assert.That(run.MainSha).IsEqualTo(CiScenario.MainSha);
        await Assert.That(run.Tree).IsEqualTo($"tree-{CiScenario.MainSha}+a");
        await Assert.That(s.Entry(7).State).IsEqualTo(CiQueueState.Testing);
        await Assert.That(s.Get<CiHealthService>().Get(s.Pipeline.Id)).IsEqualTo(new CiHealth(1, 0, 0, 1));

        s.SucceedAll(run);
        await s.ProcessQueueAsync();

        await Assert.That(s.QueueGitHub.Merged).IsEquivalentTo([7]);
        await Assert.That(s.Entry(7).State).IsEqualTo(CiQueueState.Merged);
        await Assert.That(s.QueueGitHub.TreeOf(s.QueueGitHub.Heads["main"])).IsEqualTo(run.Tree);
        await Assert.That(s.QueueGitHub.Deleted).IsEquivalentTo([run.Branch]);
        await Assert.That(s.Statuses.Posted.Where(p => p.Status.Context == CiCommitStatus.QueueContext).Select(p => (p.Sha, p.Status.State, p.Status.Description)))
            .Contains((A, "success", $"Passed in queue run #{run.Number}"));
    }

    [Test]
    public async Task A_batch_that_passes_merges_its_pull_requests_in_queue_order()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        Enqueue(s, 8);
        Enqueue(s, 7);

        await s.ProcessQueueAsync();
        var run = s.QueueRun();
        await Assert.That(run.Batch!.Select(p => p.Number)).IsEquivalentTo([8, 7], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        s.SucceedAll(run);
        await s.ProcessQueueAsync();

        await Assert.That(s.QueueGitHub.Merged).IsEquivalentTo([8, 7], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(s.Entry(7).State).IsEqualTo(CiQueueState.Merged);
        await Assert.That(s.Entry(8).State).IsEqualTo(CiQueueState.Merged);
    }

    // Bisect: [7,8,9] fails → [7,8] fails → [7] passes → [8] fails and is removed naming the step → [9] passes.
    [Test]
    public async Task A_failed_batch_is_split_until_the_pull_request_that_breaks_it_is_removed()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        s.Push(9, C);
        Enqueue(s, 7);
        Enqueue(s, 8);
        Enqueue(s, 9);

        var batches = new List<int[]>();
        for (var i = 0; i < 12 && s.Queue.Unfinished(s.Pipeline.Id).Count > 0; i++)
        {
            await s.ProcessQueueAsync();
            var run = s.QueueRun();
            if (run.Status is not RunStatus.Running)
                continue;
            var pulls = run.Batch!.Select(p => p.Number).ToArray();
            batches.Add(pulls);
            s.Succeed(run, "checkout");
            if (pulls.Contains(8))
                s.Fail(run, "test");
            else
            {
                s.Succeed(run, "build");
                s.Succeed(run, "test");
            }

            await s.ProcessQueueAsync();
        }

        await Assert.That(batches.Select(b => string.Join(",", b))).IsEquivalentTo(["7,8,9", "7,8", "7", "8", "9"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(s.QueueGitHub.Merged).IsEquivalentTo([7, 9], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var removed = s.Entry(8);
        await Assert.That((removed.State, removed.Reason, removed.FailedStep)).IsEqualTo((CiQueueState.Removed, "test failed", "test"));
        await Assert.That(s.Statuses.Posted).Contains((B, new CiCommitStatus("failure", "Removed: test failed",
            CiStatusPlanner.Link(s.Get<CiStatusOptions>(), s.Pipeline.Id, removed.Runs[0]), CiCommitStatus.QueueContext)));
        await Assert.That(s.Queue.Limit(s.Pipeline.Id)).IsEqualTo(new CiQueueLimit(s.Pipeline.Id, CiQueueService.MaxBatch, 0));
    }

    // A queue run stops at its first failed step: what else is in flight is cancelled.
    [Test]
    public async Task A_queue_run_fails_fast()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        Enqueue(s, 7);
        await s.ProcessQueueAsync();
        var run = s.QueueRun();

        s.Succeed(run, "checkout");
        s.Fail(run, "build");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
        await Assert.That(s.Latest(run, "test")!.Status).IsTypeOf<AttemptStatus.Cancelled>();
    }

    // The branch moving between two landings means the next pull request would land on a tree nobody tested.
    [Test]
    public async Task Landing_stops_when_the_branch_is_not_at_the_tree_the_batch_tested_and_the_rest_is_tested_again()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        Enqueue(s, 7);
        Enqueue(s, 8);
        await s.ProcessQueueAsync();
        var first = s.QueueRun();
        s.SucceedAll(first);
        s.QueueGitHub.AfterMerge = n =>
        {
            if (n == 7) s.QueueGitHub.Heads["main"] = "f000000000000000000000000000000000000000";
        };

        await s.ProcessQueueAsync();

        await Assert.That(s.QueueGitHub.Merged).IsEquivalentTo([7]);
        await Assert.That(s.Entry(7).State).IsEqualTo(CiQueueState.Merged);
        await Assert.That(s.Entry(8).State).IsEqualTo(CiQueueState.Queued);

        s.QueueGitHub.AfterMerge = null;
        await s.ProcessQueueAsync();
        var second = s.QueueRun();
        await Assert.That(second.Number).IsNotEqualTo(first.Number);
        await Assert.That((second.MainSha, second.Batch!.Single().Number)).IsEqualTo(("f000000000000000000000000000000000000000", 8));
    }

    [Test]
    public async Task A_push_to_the_branch_while_a_batch_is_tested_supersedes_its_run()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        Enqueue(s, 7);
        await s.ProcessQueueAsync();
        var run = s.QueueRun();

        s.QueueGitHub.Heads["main"] = "f000000000000000000000000000000000000000";
        await s.ProcessQueueAsync();

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Entry(7).State).IsEqualTo(CiQueueState.Queued);
        await Assert.That(s.QueueGitHub.Deleted).Contains(run.Branch);

        await s.ProcessQueueAsync();
        await Assert.That(s.QueueRun().MainSha).IsEqualTo("f000000000000000000000000000000000000000");
    }

    // Auto-merge keeps asking for the queue: a fix pushed after a failure goes back in without touching the toggle.
    [Test]
    public async Task Auto_merge_enqueues_each_new_head_until_it_is_turned_off()
    {
        await using var s = new CiScenario();

        s.Push(7, A, autoMerge: true);
        var entry = s.Entry(7);
        await Assert.That((entry.State, entry.Via, entry.HeadSha)).IsEqualTo((CiQueueState.Queued, CiQueueVia.AutoMerge, A));

        await s.ProcessQueueAsync();
        var run = s.QueueRun();
        s.Succeed(run, "checkout");
        s.Fail(run, "test");
        await s.ProcessQueueAsync();
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Push(7, A, autoMerge: true);
        await Assert.That((s.Entry(7).State, s.Entry(7).Reason)).IsEqualTo((CiQueueState.Removed, "test failed"));

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Push(7, D, autoMerge: true);
        await Assert.That((s.Entry(7).State, s.Entry(7).HeadSha)).IsEqualTo((CiQueueState.Queued, D));

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Push(7, B, autoMerge: true);
        await Assert.That((s.Entry(7).State, s.Entry(7).HeadSha)).IsEqualTo((CiQueueState.Queued, B));
        await Assert.That(s.Queue.ListByPipeline(s.Pipeline.Id).Select(e => (e.HeadSha, e.State, e.Reason)))
            .IsEquivalentTo([(A, CiQueueState.Removed, "test failed"), (D, CiQueueState.Removed, "pushed"), (B, CiQueueState.Queued, (string?)null)],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Push(7, B);
        await Assert.That((s.Entry(7).State, s.Entry(7).Reason)).IsEqualTo((CiQueueState.Removed, "auto-merge turned off"));
    }

    // Pushing to a pull request in a batch under test: the push isn't what was tested, so it leaves; the rest is tested again.
    [Test]
    public async Task A_push_to_a_queued_pull_request_removes_it_and_cancels_its_batch()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        Enqueue(s, 7);
        Enqueue(s, 8);
        await s.ProcessQueueAsync();
        var run = s.QueueRun();

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        s.Push(7, D);

        await Assert.That((s.Entry(7).State, s.Entry(7).Reason)).IsEqualTo((CiQueueState.Removed, "pushed"));
        await Assert.That((s.Get(run).Status, s.Get(run).CancelReason)).IsEqualTo((RunStatus.Cancelled, CiCancelReason.Dequeued));
        await Assert.That(s.Entry(8).State).IsEqualTo(CiQueueState.Queued);
    }

    [Test]
    public async Task Dequeueing_names_who_did_it()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        Enqueue(s, 7);

        var removed = TestRunDomain.Pick(s.QueueActions.Dequeue(s.Pipeline.Id, 7, "oliver"));

        await Assert.That((removed.State, removed.Reason)).IsEqualTo((CiQueueState.Removed, "dequeued by oliver"));
        await s.QueueActions.Dequeue(s.Pipeline.Id, 7, "oliver").Problems.IsSingle<NotFoundProblem>("Pull request #7 is not in the merge queue.");
    }

    // A restart (or a lost answer) mid-landing: the merges already done stay done, the next one is not repeated.
    [Test]
    public async Task Landing_resumes_where_it_stopped_without_merging_twice()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        Enqueue(s, 7);
        Enqueue(s, 8);
        await s.ProcessQueueAsync();
        s.SucceedAll(s.QueueRun());
        s.QueueGitHub.MergeAnswers.Enqueue((GitHubWriteOutcome.Done, false));
        s.QueueGitHub.MergeAnswers.Enqueue((GitHubWriteOutcome.Retry, Lost: true));

        await s.ProcessQueueAsync();
        await Assert.That(s.Entry(7).State).IsEqualTo(CiQueueState.Merged);
        await Assert.That(s.Entry(8).State).IsEqualTo(CiQueueState.Testing);

        await s.ProcessQueueAsync();
        await Assert.That(s.Entry(8).State).IsEqualTo(CiQueueState.Merged);
        await Assert.That(s.QueueGitHub.Merged).IsEquivalentTo([7, 8], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_pull_request_that_conflicts_with_the_batch_is_removed_and_the_rest_tested()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        Enqueue(s, 7);
        Enqueue(s, 8);
        s.QueueGitHub.Conflicts.Add(B);

        await s.ProcessQueueAsync();
        await Assert.That((s.Entry(8).State, s.Entry(8).Reason)).IsEqualTo((CiQueueState.Removed, "conflict"));

        await s.ProcessQueueAsync();
        await Assert.That(s.QueueRun().Batch!.Select(p => p.Number)).IsEquivalentTo([7]);
    }

    [Test]
    public async Task GitHub_refusing_the_merge_removes_the_pull_request_with_its_message()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        Enqueue(s, 7);
        await s.ProcessQueueAsync();
        s.SucceedAll(s.QueueRun());
        s.QueueGitHub.MergeAnswers.Enqueue((GitHubWriteOutcome.Refused, false));

        await s.ProcessQueueAsync();

        await Assert.That(s.Entry(7).Reason).IsEqualTo("GitHub refused the merge: Required status check \"olve/merge-queue\" is expected.");
        await Assert.That(s.QueueGitHub.Merged).IsEmpty();
    }

    [Test]
    public async Task The_queue_lists_entries_in_order_with_their_position_then_finished_ones()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        s.Push(8, B);
        s.Push(9, C);
        Enqueue(s, 7);
        Enqueue(s, 8);
        Enqueue(s, 9);
        TestRunDomain.Pick(s.QueueActions.Dequeue(s.Pipeline.Id, 8, "oliver"));

        var queue = s.Get<CiRunViewService>().Queue(s.Pipeline.Id);

        await Assert.That(queue.Entries.Select(e => (e.Pull, e.Position))).IsEquivalentTo([(7, (int?)1), (9, 2)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(queue.Finished.Select(e => (e.Pull, e.State, e.Reason)))
            .IsEquivalentTo([(8, Contract.QueueEntryState.Removed, (string?)"dequeued by oliver")]);
    }

    private static EnqueuePullHandler Handler(CiScenario s) => new(
        s.Get<PipelineService>(), s.Get<PipelineConfigBindingService>(), s.Get<PullRequestService>(), s.GitHub, s.Get<CiOptions>(), s.QueueActions,
        s.Get<CiRunViewService>(), s.Get<CiPersistenceService>(), new HttpContextAccessor(), s.Clock);
}
