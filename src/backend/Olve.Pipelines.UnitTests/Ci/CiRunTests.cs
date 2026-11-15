using System.Text.Json;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>A pull request's CI runs: from the webhook's report through debounce, start, stages and their end.</summary>
public class CiRunTests
{
    private const string A = "a000000000000000000000000000000000000000";
    private const string B = "b000000000000000000000000000000000000000";

    [Test]
    public async Task A_burst_of_pushes_makes_one_run_for_the_last_once_the_head_was_still_for_a_minute()
    {
        await using var s = new CiScenario();

        s.Push(7, A);
        s.Clock.Advance(TimeSpan.FromSeconds(30));
        await s.Trigger.StartDueAsync(default);
        s.Push(7, B);
        s.Clock.Advance(TimeSpan.FromSeconds(59));
        await s.Trigger.StartDueAsync(default);
        await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 7)).IsEmpty();

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        await s.Trigger.StartDueAsync(default);

        var run = await Single(s, 7);
        await Assert.That(run.HeadSha).IsEqualTo(B);
        await Assert.That(run.TestedSha).IsEqualTo(CiScenario.Merge(B));
        await Assert.That(run.Tree).IsEqualTo("tree-" + CiScenario.Merge(B));
        await Assert.That(run.MainSha).IsEqualTo(CiScenario.MainSha);
        await Assert.That(run.Number).IsEqualTo(1);
        await Assert.That(JsonDocument.Parse(run.Input).RootElement.GetProperty("sha").GetString()).IsEqualTo(CiScenario.Merge(B));
        await Assert.That(s.Active(run)).IsEquivalentTo(["checkout"]);
    }

    // The whole path in one: GitHub's signed delivery → the tracked pull request → debounce → a run → its steps → succeeded.
    [Test]
    public async Task A_signed_pull_request_delivery_leads_to_a_run_that_succeeds()
    {
        await using var s = new CiScenario();
        var pull = PullRequestFixtures.Pull(7, headSha: A, mergeable: true, mergeCommitSha: CiScenario.Merge(A), updatedAt: s.Clock.GetUtcNow());
        s.GitHub.Answer(pull);
        var body = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["action"] = "opened",
            ["number"] = 7,
            ["pull_request"] = PullRequestFixtures.Json(p =>
            {
                p["number"] = 7;
                p["state"] = "open";
                p["merged_at"] = null;
                p["head"]!["sha"] = A;
                p["updated_at"] = s.Clock.GetUtcNow().ToString("O");
            }),
        }.ToJsonString());
        var handler = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Olve.Pipelines.Pipelines.Sync.BindingWebhookHandler>(
            s.Provider, new Microsoft.AspNetCore.Http.HttpContextAccessor());

        var response = await handler.HandleAsync(new Olve.Pipelines.Api.BindingWebhooksGithubRequest(s.Binding.Id.Value.Value,
            Olve.Pipelines.GitHub.GitHubWebhookSignature.Compute(s.Binding.WebhookSecret!, body), "pull_request", new MemoryStream(body)), default);
        await Assert.That(response).IsTypeOf<Olve.Pipelines.Api.BindingWebhooksGithubResponse.Accepted>();
        await Assert.That(s.Trigger.IsPending(s.Pipeline.Id, 7)).IsTrue();
        await s.SettleAsync();
        var run = await Single(s, 7);
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        await Assert.That(response).IsTypeOf<Olve.Pipelines.Api.BindingWebhooksGithubResponse.Accepted>();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task A_new_push_supersedes_the_running_run_at_once_and_cancels_its_attempts()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var first = s.Run(7);

        s.Push(7, B);
        await s.SettleAsync();

        var second = s.Run(7);
        await Assert.That(second.Number).IsEqualTo(2);
        await Assert.That(s.Get(first).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Get(first).SupersededByNumber).IsEqualTo(2);
        await Assert.That(s.Latest(first, "checkout")!.Status).IsTypeOf<Cancelled>();
        await Assert.That(s.Active(second)).IsEquivalentTo(["checkout"]);
    }

    [Test]
    [Arguments(true, CiCancelReason.Merged)]
    [Arguments(false, CiCancelReason.Closed)]
    public async Task Closing_or_merging_cancels_the_running_run(bool merged, CiCancelReason reason)
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();

        s.Close(7, A, merged);

        var run = s.Run(7);
        await Assert.That(run.Status).IsEqualTo(RunStatus.Cancelled);
        await Assert.That(run.CancelReason).IsEqualTo(reason);
        await Assert.That(s.Active(run)).IsEmpty();
    }

    [Test]
    public async Task A_pull_request_closed_before_its_debounce_passed_gets_no_run()
    {
        await using var s = new CiScenario();
        s.Push(7, A);

        s.Close(7, A, merged: false);
        await s.SettleAsync();

        await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 7)).IsEmpty();
    }

    [Test]
    public async Task A_pull_request_that_doesnt_merge_gets_no_run_and_shows_a_conflict()
    {
        await using var s = new CiScenario();
        s.Push(7, A, mergeable: false);

        await s.SettleAsync();

        await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 7)).IsEmpty();
        await Assert.That(s.Get<CiRunViewService>().Pull(Tracked(s, 7)).Conflict).IsTrue();
    }

    [Test]
    public async Task GitHub_is_asked_again_until_it_has_decided_whether_the_pull_request_merges()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        var decided = s.GitHub.Pulls[7].Peek();
        s.GitHub.Answer(decided with { Mergeable = null }, decided with { Mergeable = null }, decided);

        var start = s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7);
        while (!start.IsCompleted)
        {
            await Task.Yield();
            s.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var started = await start;
        await Assert.That(started).Succeeded();
        await Assert.That(TestRunDomain.Pick(started).Run!.HeadSha).IsEqualTo(A);
    }

    [Test]
    public async Task GitHub_never_deciding_starts_no_run()
    {
        await using var s = new CiScenario();
        s.Push(7, A, mergeable: null);

        var start = s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7);
        while (!start.IsCompleted)
        {
            await Task.Yield();
            s.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var result = await start;
        await result.Problems.IsSingle<UnavailableProblem>("GitHub hasn't decided whether pull request #7 merges.");
    }

    [Test]
    public async Task Stages_run_one_after_another_and_a_failed_step_fails_the_run_at_the_barrier()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);

        s.Succeed(run, "checkout", """{"tree":{"run":1,"step":"checkout","attempt":1,"path":"src/","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}""");
        await Assert.That(s.Active(run)).IsEquivalentTo(["build", "test"]);

        s.Fail(run, "build");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);

        s.Succeed(run, "test");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task Every_step_succeeding_succeeds_the_run()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);

        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task A_skipped_step_skips_the_steps_that_need_its_output()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);

        s.Skip(run, "checkout", "nothing to check out");

        await Assert.That(s.Latest(run, "build")!.Status).IsTypeOf<Skipped>();
        await Assert.That(((Skipped)s.Latest(run, "build")!.Status).Reason).IsEqualTo("input 'source' needs checkout (skipped: nothing to check out)");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task A_step_with_retries_runs_again_after_a_failure()
    {
        await using var s = new CiScenario(node =>
        {
            ManifestFixture.Step(node, "checkout")["retries"] = 1;
            ManifestFixture.CiStep(node, "checkout")["retries"] = 1;
        });
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);

        s.Fail(run, "checkout");

        await Assert.That(s.Attempts.ListForStep(run.Id, "checkout").Select(a => a.Status.Discriminator())).IsEquivalentTo(["failed", "scheduled"]);
        s.Fail(run, "checkout");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task A_step_whose_when_doesnt_match_the_CI_input_is_excluded()
    {
        await using var s = new CiScenario(node =>
        {
            var stages = node["ci"]!["stages"]!.AsArray();
            var test = stages[1]!.DeepClone();
            stages[1] = new System.Text.Json.Nodes.JsonObject
            {
                ["when"] = new System.Text.Json.Nodes.JsonObject { ["repo"] = new System.Text.Json.Nodes.JsonArray("someone/else") },
                ["stages"] = new System.Text.Json.Nodes.JsonArray(test),
            };
        });
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);

        s.Succeed(run, "checkout");

        await Assert.That(s.Latest(run, "build")).IsNull();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task A_version_without_CI_starts_no_run()
    {
        await using var s = new CiScenario(node => node.Remove("ci"));
        s.Push(7, A);

        var started = await s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7);
        await Assert.That(started).Succeeded();
        await Assert.That(TestRunDomain.Pick(started).Skipped).IsEqualTo(CiStartSkip.NoCi);
        await Assert.That(s.Runs.List()).IsEmpty();
    }

    [Test]
    public async Task A_head_that_already_has_a_run_doesnt_get_another_automatically()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();

        s.Push(7, A, draft: true);
        await s.SettleAsync();

        await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 7)).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(false, JobPriority.ReadyPull)]
    [Arguments(true, JobPriority.DraftPull)]
    public async Task A_draft_pull_requests_attempts_go_after_a_ready_ones(bool draft, JobPriority priority)
    {
        await using var s = new CiScenario();
        s.Push(7, A, draft: draft);
        await s.SettleAsync();

        var attempt = s.Latest(s.Run(7), "checkout")!;

        await Assert.That(JobRunner.PriorityOf(attempt, s.Runs, s.Get<PullRequestService>())).IsEqualTo(priority);
    }

    [Test]
    public async Task A_stale_result_is_one_tested_on_a_main_that_has_moved()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var views = s.Get<CiRunViewService>();

        s.Get<Olve.Pipelines.Pipelines.Sync.PipelineConfigBindingService>().SetLastPushSha(s.Binding.Id, CiScenario.MainSha).DiscardResult();
        await Assert.That(views.Pull(Tracked(s, 7)).Stale).IsFalse();

        s.Get<Olve.Pipelines.Pipelines.Sync.PipelineConfigBindingService>().SetLastPushSha(s.Binding.Id, new string('9', 40)).DiscardResult();
        await Assert.That(views.Pull(Tracked(s, 7)).Stale).IsTrue();
        await Assert.That(views.Pull(Tracked(s, 7)).LatestRun!.Number).IsEqualTo(1);
    }

    private static async Task<CiRun> Single(CiScenario s, int pull)
    {
        var runs = s.Runs.ListByPull(s.Pipeline.Id, pull);
        await Assert.That(runs).Count().IsEqualTo(1);
        return runs[0];
    }

    private static PullRequest Tracked(CiScenario s, int number) =>
        s.Get<PullRequestService>().TryGet(s.Pipeline.Id, number, out var pull) ? pull : throw new InvalidOperationException("not tracked");
}
