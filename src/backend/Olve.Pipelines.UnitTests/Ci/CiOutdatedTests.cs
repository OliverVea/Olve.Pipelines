using System.Text.Json.Nodes;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Manifests;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>When the active version checks pull requests differently, open ones whose result came from the old checks run again.</summary>
public class CiOutdatedTests
{
    private const string A = "a000000000000000000000000000000000000000";
    private const string B = "b111111111111111111111111111111111111111";
    private const string C = "c222222222222222222222222222222222222222";
    private static readonly string Hash1 = "sha256:" + new string('1', 64);
    private static readonly string Hash2 = "sha256:" + new string('2', 64);

    private static CiScenario Scenario() => new(node => node["codeHash"] = Hash1);

    /// <summary>A new version, switched to: <paramref name="change"/> edits its manifest (the code hash stays unless it changes it).</summary>
    private static PipelineVersion Activate(CiScenario s, Action<JsonObject> change)
    {
        var version = TestRunDomain.Pick(s.Get<PipelineVersionService>().Add(s.Pipeline.Id, ManifestFixture.Edit(node =>
        {
            ManifestFixture.WithCi(node);
            node["codeHash"] = Hash1;
            change(node);
        }), "sha256:" + Guid.NewGuid().ToString("N"), "3333333333333333333333333333333333333333",
            new Dictionary<string, int>(), new Dictionary<string, int>()));
        TestRunDomain.Pick(s.Get<SwitchService>().Switch(s.Pipeline.Id, version.Id));
        return version;
    }

    public static IEnumerable<Func<(string Rule, Action<JsonObject> Change, bool RunsAgain)>> Changes()
    {
        yield return () => ("new step code (the code hash)", n => n["codeHash"] = Hash2, true);
        yield return () => ("a changed CI step", n => ManifestFixture.CiStep(n, "test")["image"] = "alpine:3.23", true);
        yield return () => ("CI removed: nothing to rerun with", n => n.Remove("ci"), false);
        yield return () => ("only the deploy side changed", n => n["description"] = "deploys differently", false);
    }

    [Test]
    [MethodDataSource(nameof(Changes))]
    public async Task An_open_pull_request_runs_again_when_the_checks_change(string rule, Action<JsonObject> change, bool runsAgain)
    {
        await using var s = Scenario();
        s.Push(7, A);
        await s.SettleAsync();
        var first = s.Run(7);
        s.SucceedAll(first);

        var version = Activate(s, change);
        await s.Trigger.StartOutdatedAsync(CancellationToken.None);

        var runs = s.Runs.ListByPull(s.Pipeline.Id, 7);
        await Assert.That(runs.Count).IsEqualTo(runsAgain ? 2 : 1);
        if (runsAgain)
        {
            var again = s.Run(7);
            await Assert.That(again.VersionId).IsEqualTo(version.Id);
            await Assert.That(again.HeadSha).IsEqualTo(A);
            await Assert.That(again.Outdated).IsTrue();
            await Assert.That(JobRunner.PriorityOf(s.Latest(again, "checkout")!, s.Runs, s.Get<PullRequestService>()))
                .IsEqualTo(JobPriority.OutdatedPull);
        }
    }

    [Test]
    public async Task Drafts_and_pull_requests_on_their_own_definition_are_left_alone()
    {
        await using var s = Scenario();
        s.Push(7, A, draft: true);
        s.GitHub.ChangedFiles[CiScenario.Merge(B)] = [".pipelines/ci.ts"];
        s.Push(8, B);
        await s.SettleAsync();

        Activate(s, n => n["codeHash"] = Hash2);
        await s.Trigger.StartOutdatedAsync(CancellationToken.None);

        await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 7).Count).IsEqualTo(1);
        await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 8).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_running_run_on_the_old_checks_is_superseded_by_the_new_one()
    {
        await using var s = Scenario();
        s.Push(7, A);
        await s.SettleAsync();
        var first = s.Run(7);

        Activate(s, n => n["codeHash"] = Hash2);
        await s.Trigger.StartOutdatedAsync(CancellationToken.None);

        await Assert.That(s.Get(first).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Run(7).Outdated).IsTrue();
    }

    [Test]
    public async Task A_push_waiting_out_its_debounce_gets_its_ordinary_run_instead()
    {
        await using var s = Scenario();
        s.Push(7, A);
        await s.SettleAsync();
        s.Push(7, C);

        Activate(s, n => n["codeHash"] = Hash2);
        await s.Trigger.StartOutdatedAsync(CancellationToken.None);
        await s.SettleAsync();

        var runs = s.Runs.ListByPull(s.Pipeline.Id, 7);
        await Assert.That(runs.Count).IsEqualTo(2);
        await Assert.That(s.Run(7).HeadSha).IsEqualTo(C);
        await Assert.That(s.Run(7).Outdated).IsFalse();
    }
}
