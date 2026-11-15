using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>The <c>olve/ci</c> commit status: what a run reports, and how it reaches GitHub.</summary>
public class CiStatusTests
{
    private const string A = "a000000000000000000000000000000000000000";
    private const string B = "b000000000000000000000000000000000000000";

    private static readonly Dictionary<string, Action<CiScenario, CiRun>> Acts = new()
    {
        ["started"] = (_, _) => { },
        ["a step done"] = (s, run) => s.Succeed(run, "checkout"),
        ["succeeded"] = (s, run) =>
        {
            s.Succeed(run, "checkout");
            s.Succeed(run, "build");
            s.Clock.Advance(TimeSpan.FromSeconds(372));
            s.Succeed(run, "test");
        },
        ["a step failed"] = (s, run) =>
        {
            s.Succeed(run, "checkout");
            s.Succeed(run, "build");
            s.Fail(run, "test");
        },
        ["its definition can't be used"] = (s, run) =>
            TestRunDomain.Pick(s.Runs.FailDefinition(run.Id, ["Build/describe failed: the build failed (exit 1)."])),
        ["closed while running: nothing after its start"] = (s, _) => s.Close(7, A, merged: false),
    };

    public static IEnumerable<Func<(string Case, string Expected)>> RunStates()
    {
        yield return () => ("started", "pending: 0 of 3 steps done");
        yield return () => ("a step done", "pending: 1 of 3 steps done");
        yield return () => ("succeeded", "success: Succeeded in 6m12s");
        yield return () => ("a step failed", "failure: Failed: test");
        yield return () => ("its definition can't be used", "failure: Failed: Build/describe failed: the build failed (exit 1).");
        yield return () => ("closed while running: nothing after its start", "pending: 0 of 3 steps done");
    }

    [Test]
    [MethodDataSource(nameof(RunStates))]
    public async Task A_run_reports_its_state_on_the_pull_requests_head(string @case, string expected)
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();

        Acts[@case](s, s.Run(7));
        await s.Get<CiStatusDelivery>().DrainAsync();

        var (sha, status) = s.Statuses.Posted.Last();
        await Assert.That(sha).IsEqualTo(A);
        await Assert.That($"{status.State}: {status.Description}").IsEqualTo(expected);
    }

    [Test]
    public async Task A_superseded_run_reports_nothing_more_and_the_run_replacing_it_reports_on_its_head()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        await s.Get<CiStatusDelivery>().DrainAsync();

        s.Push(7, B);
        await s.SettleAsync();
        await s.Get<CiStatusDelivery>().DrainAsync();

        await Assert.That(s.Statuses.Posted.Select(p => $"{p.Sha[..1]} {p.Status.State}: {p.Status.Description}"))
            .IsEquivalentTo(["a pending: 0 of 3 steps done", "b pending: 0 of 3 steps done"]);
    }

    [Test]
    public async Task States_waiting_to_be_sent_coalesce_into_the_latest()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");

        await s.Get<CiStatusDelivery>().DrainAsync();

        await Assert.That(s.Statuses.Posted.Select(p => p.Status.Description)).IsEquivalentTo(["2 of 3 steps done"]);
    }

    [Test]
    public async Task A_state_that_changes_while_one_is_being_sent_is_sent_after_it()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);
        var once = true;
        s.Statuses.DuringPost = () =>
        {
            if (!once) return;
            once = false;
            s.Succeed(run, "checkout");
        };

        await s.Get<CiStatusDelivery>().DrainAsync();

        await Assert.That(s.Statuses.Posted.Select(p => p.Status.Description))
            .IsEquivalentTo(["0 of 3 steps done", "1 of 3 steps done"]);
    }

    [Test]
    public async Task A_server_error_is_tried_again_after_a_backoff()
    {
        await using var s = new CiScenario();
        s.Statuses.Outcomes.Enqueue((CommitStatusOutcome.Retry, 502));
        s.Push(7, A);
        await s.SettleAsync();

        var delivering = s.Get<CiStatusDelivery>().DrainAsync();
        while (s.Clock.Pending == 0)
            await Task.Yield();
        await Assert.That(s.Statuses.Posted).IsEmpty();
        s.Clock.Advance(CiStatusDelivery.Backoff[0]);
        await delivering;

        await Assert.That(s.Statuses.Posted.Select(p => p.Status.Description)).IsEquivalentTo(["0 of 3 steps done"]);
    }

    [Test]
    public async Task A_token_that_cant_write_statuses_warns_on_the_binding_until_a_status_gets_through()
    {
        await using var s = new CiScenario();
        s.Statuses.Outcomes.Enqueue((CommitStatusOutcome.Denied, 403));
        s.Push(7, A);
        await s.SettleAsync();

        await s.Get<CiStatusDelivery>().DrainAsync();

        await Assert.That(s.Statuses.Outcomes).IsEmpty();
        await Assert.That(s.Get<CiStatusWarnings>().For(s.Pipeline.Id)).IsEquivalentTo([
            "CI can't post commit statuses to 'OliverVea/Olve.Pipelines' (GitHub returned 403): the binding's token needs Commit statuses: write.",
        ]);

        s.Succeed(s.Run(7), "checkout");
        await s.Get<CiStatusDelivery>().DrainAsync();

        await Assert.That(s.Get<CiStatusWarnings>().For(s.Pipeline.Id)).IsEmpty();
    }

    [Test]
    public async Task A_status_links_the_CI_run_page()
    {
        await using var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();

        var status = new CiStatusPlanner(s.Get<CiRunViewService>(), new CiStatusOptions("https://pipelines.example/")).For(s.Run(7));

        await Assert.That(status!.TargetUrl).IsEqualTo($"https://pipelines.example/pipeline/{s.Pipeline.Id.Value.Value:D}/ci/run/1");
    }
}
