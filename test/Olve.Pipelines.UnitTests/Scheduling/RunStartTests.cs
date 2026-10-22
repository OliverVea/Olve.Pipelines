using Olve.Results.TUnit;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>Run starts: concurrent runs, automatic-trigger de-dup, latest-wins across runs, whole-run reruns.</summary>
public class RunStartTests
{
    [Test]
    public async Task AutomaticRun_LeavesOlderRunningRunsRunning_TheyFlowOnConcurrently()
    {
        var s = new SchedulerScenario();
        var older = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.Succeed(older, "checkout");
        s.MarkInProgress(older, "build");

        var newer = s.Start(Input(sha: OtherSha), RunTriggerKind.Push);

        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Latest(older, "test")!.Status).IsTypeOf<Scheduled>();
        await Assert.That(s.Active(newer)).IsEquivalentTo(["checkout"]);

        // The older run moves on to its next stage while the newer one is still behind it.
        s.Succeed(older, "build");
        s.Succeed(older, "test");
        await Assert.That(s.Active(older)).IsEquivalentTo(["deploy-beta"]);
        await Assert.That(s.Get(newer).Status).IsEqualTo(RunStatus.Running);
    }

    [Test]
    public async Task AutomaticRun_ObsoletesOlderRunsPendingAttempt_WithoutFailingItself()
    {
        var s = new SchedulerScenario();
        var older = s.Start(Input(sha: Sha), RunTriggerKind.Poll); // checkout Scheduled, not yet running

        var newer = s.Start(Input(sha: OtherSha), RunTriggerKind.Push);

        // Both checkouts waited: latest-wins obsoleted the older one, which supersedes its run.
        await Assert.That(s.Latest(older, "checkout")!.Status).IsTypeOf<Obsolete>();
        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Get(older).SupersededBy).IsEqualTo(newer.Id);
        await Assert.That(s.Get(newer).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Latest(newer, "checkout")!.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ManualRun_SupersedesNothing()
    {
        var s = new SchedulerScenario();
        var first = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.MarkInProgress(first, "checkout");

        var manual = s.Start(Input(sha: OtherSha));

        await Assert.That(s.Get(first).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Latest(first, "checkout")!.Status).IsTypeOf<InProgress>();
        // Same key: the manual run's checkout waits behind the in-flight one (busy key), still Scheduled.
        await Assert.That(s.Latest(manual, "checkout")!.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task LatestWinsAcrossRuns_PendingAttemptOfMiddleRunIsObsoleted_AndThatRunSuperseded()
    {
        var s = new SchedulerScenario();
        var run1 = s.Start(Input(sha: Sha));
        s.MarkInProgress(run1, "checkout");
        var run2 = s.Start(Input(sha: Sha)); // checkout Scheduled behind run1's
        var run3 = s.Start(Input(sha: OtherSha));

        await Assert.That(s.Latest(run2, "checkout")!.Status).IsTypeOf<Obsolete>();
        await Assert.That(s.Get(run2).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Get(run2).SupersededBy).IsEqualTo(run3.Id);
        await Assert.That(s.Latest(run3, "checkout")!.Status).IsTypeOf<Scheduled>();

        // The in-flight run is untouched and moves on.
        s.Succeed(run1, "checkout");
        await Assert.That(s.Active(run1)).IsEquivalentTo(["build", "test"]);
        await Assert.That(s.Get(run3).Status).IsEqualTo(RunStatus.Running);
    }

    [Test]
    public async Task Dedup_SameMappedInputFromPushAndPoll_YieldsOneRun()
    {
        var s = new SchedulerScenario();
        var pushed = s.Start(Input(sha: Sha), RunTriggerKind.Push);

        var polled = s.StartOutcome(Input(sha: Sha), RunTriggerKind.Poll);

        await Assert.That(polled).IsTypeOf<RunStartOutcome.Deduplicated>();
        await Assert.That(((RunStartOutcome.Deduplicated)polled).Existing.Id).IsEqualTo(pushed.Id);
        await Assert.That(s.D.RunService.ListByPipeline(s.Pipeline.Id)).Count().IsEqualTo(1);
        // The default key is the canonical hash of the mapped input.
        using var doc = System.Text.Json.JsonDocument.Parse(Input(sha: Sha));
        await Assert.That(pushed.Trigger.DedupKey).IsEqualTo(CanonicalJson.Hash(doc.RootElement));
    }

    [Test]
    public async Task Dedup_AppliesToRecentlyFinishedRuns_UntilTheWindowPasses()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);

        await Assert.That(s.StartOutcome(Input(sha: Sha), RunTriggerKind.Poll)).IsTypeOf<RunStartOutcome.Deduplicated>();

        s.Clock.Advance(RunStartService.DedupWindow + TimeSpan.FromMinutes(1));
        await Assert.That(s.StartOutcome(Input(sha: Sha), RunTriggerKind.Poll)).IsTypeOf<RunStartOutcome.Started>();
    }

    [Test]
    public async Task Dedup_ComparesWithLatestAutomaticRunOnly_SoABAStartsAgain()
    {
        var s = new SchedulerScenario();
        s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.Start(Input(sha: OtherSha), RunTriggerKind.Push); // supersedes the first

        var again = s.StartOutcome(Input(sha: Sha), RunTriggerKind.Push);

        await Assert.That(again).IsTypeOf<RunStartOutcome.Started>();
    }

    [Test]
    public async Task Dedup_ExplicitKeyIsUsed_AndManualRunsAreNeverDeduplicated()
    {
        var s = new SchedulerScenario();
        s.StartOutcome(Input(sha: Sha), RunTriggerKind.Push, dedupKey: "commit:a");

        await Assert.That(s.StartOutcome(Input(sha: Sha, deployTo: "prod"), RunTriggerKind.Poll, dedupKey: "commit:a"))
            .IsTypeOf<RunStartOutcome.Deduplicated>();
        await Assert.That(s.StartOutcome(Input(sha: Sha))).IsTypeOf<RunStartOutcome.Started>();
        await Assert.That(s.StartOutcome(Input(sha: Sha))).IsTypeOf<RunStartOutcome.Started>();
    }

    [Test]
    public async Task RerunRun_CreatesLinkedRunWithSameInput_BypassingDedup()
    {
        var s = new SchedulerScenario();
        var original = s.Start(Input(sha: Sha), RunTriggerKind.Push);

        var outcome = TestRunDomain.Pick(s.D.Starts.RerunRun(original.Id));

        await Assert.That(outcome).IsTypeOf<RunStartOutcome.Started>();
        var rerun = ((RunStartOutcome.Started)outcome).Run;
        await Assert.That(rerun.RerunOf).IsEqualTo(original.Id);
        await Assert.That(rerun.Input).IsEqualTo(original.Input);
        await Assert.That(rerun.Trigger.Kind).IsEqualTo(RunTriggerKind.Manual);
        await Assert.That(s.Latest(rerun, "checkout")!.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task Start_RejectsNonObjectInput()
    {
        var s = new SchedulerScenario();

        var result = s.D.Starts.Start(s.Pipeline.Id, "[1,2]", new RunTrigger(RunTriggerKind.Manual));

        await Assert.That(result).Failed();
    }
}
