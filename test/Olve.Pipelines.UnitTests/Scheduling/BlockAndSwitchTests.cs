using Olve.Results.TUnit;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>Blocked steps (hold/resume) and version switches (draining queue, completion, cancel).</summary>
public class BlockAndSwitchTests
{
    [Test]
    public async Task BlockedStep_HoldsRunAtItsStage_UnblockResumes()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var run = s.Start(Input(deployTo: "prod"));

        s.SucceedThroughDeployBeta(run);

        var held = s.Get(run);
        await Assert.That(held.Status).IsEqualTo(RunStatus.Running);
        await Assert.That(held.HeldAt).IsEqualTo("deploy-prod");
        await Assert.That(s.Latest(run, "deploy-prod")).IsNull();

        await Assert.That(s.D.GateService.Unblock(s.Pipeline.Id, "deploy-prod")).Succeeded();

        await Assert.That(s.Get(run).HeldAt).IsNull();
        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-prod"]);
        s.Succeed(run, "deploy-prod");
        s.Succeed(run, "publish-cli");
        s.Succeed(run, "summary");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task BlockedStep_WhoseWhenFails_IsExcludedNotHeld()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var run = s.Start(Input(deployTo: "beta"));

        s.SucceedThroughDeployBeta(run);

        await Assert.That(s.Get(run).HeldAt).IsNull();
        await Assert.That(s.Latest(run, "deploy-prod")).IsNull();
    }

    [Test]
    public async Task BlockingAfterAttemptExists_DoesNotStopIt_AndRerunOfBlockedStepIsRejected()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-beta")).Succeeded();

        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Scheduled>();
        s.Fail(run, "deploy-beta");
        await Assert.That(s.D.Continuations.RerunStep(run.Id, "deploy-beta", continueAfter: true)).Failed();
        await Assert.That(s.D.GateService.ListBlocked(s.Pipeline.Id)).IsEquivalentTo(["deploy-beta"]);
    }

    [Test]
    public async Task Draining_QueuesStartLatestWins_SwitchCompletesWhenOldWorkFinishes_QueuedRunStartsOnNewVersion()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();

        var switching = TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));
        await Assert.That(switching).IsTypeOf<PipelineActivation.Draining>();

        var first = s.StartOutcome(Input(sha: OtherSha), RunTriggerKind.Push);
        var second = s.StartOutcome(Input(sha: new string('c', 40)), RunTriggerKind.Push);
        await Assert.That(first).IsTypeOf<RunStartOutcome.Queued>();
        await Assert.That(second).IsTypeOf<RunStartOutcome.Queued>();

        // The old run's in-flight step finishes, but the draining version starts nothing new.
        s.Succeed(old, "checkout");

        await Assert.That(s.D.ActivationService.TryGet(s.Pipeline.Id, out var activation)).IsTrue();
        await Assert.That(activation).IsTypeOf<PipelineActivation.Active>();
        await Assert.That(((PipelineActivation.Active)activation!).VersionId).IsEqualTo(v2.Id);
        await Assert.That(s.Latest(old, "build")).IsNull();
        await Assert.That(s.Get(old).Status).IsEqualTo(RunStatus.Cancelled); // un-continuable on v2

        // Only the latest queued request started, on v2.
        var runs = s.D.RunService.ListByPipeline(s.Pipeline.Id);
        await Assert.That(runs).Count().IsEqualTo(2);
        var started = runs[0];
        await Assert.That(started.VersionId).IsEqualTo(v2.Id);
        await Assert.That(started.Input).Contains(new string('c', 40));
        await Assert.That(s.Latest(started, "checkout")!.Status).IsTypeOf<Scheduled>();
        await Assert.That(s.D.PendingStarts.TryGet(s.Pipeline.Id, out _)).IsFalse();
    }

    [Test]
    public async Task Draining_SameTriggerAsOldVersionRun_IsQueuedNotDeduplicated_AndRunsOnNewVersion()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));

        // Redelivery of the same push while draining: the v1 run is about to be abandoned.
        await Assert.That(s.StartOutcome(Input(sha: Sha), RunTriggerKind.Push)).IsTypeOf<RunStartOutcome.Queued>();

        s.Succeed(old, "checkout");

        var newest = s.D.RunService.ListByPipeline(s.Pipeline.Id)[0];
        await Assert.That(newest.Id).IsNotEqualTo(old.Id);
        await Assert.That(newest.VersionId).IsEqualTo(v2.Id);
        await Assert.That(s.Latest(newest, "checkout")!.Status).IsTypeOf<Scheduled>();
        await Assert.That(s.Get(old).Status).IsEqualTo(RunStatus.Cancelled);

        // ...and a later identical trigger is de-duplicated against the v2 run.
        await Assert.That(s.StartOutcome(Input(sha: Sha), RunTriggerKind.Poll)).IsTypeOf<RunStartOutcome.Deduplicated>();
    }

    [Test]
    public async Task Switch_WithNothingInFlight_CompletesImmediately_HeldRunDoesNotBlockAndIsCancelled()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var held = s.Start(Input(deployTo: "prod"));
        s.SucceedThroughDeployBeta(held);
        await Assert.That(s.Get(held).HeldAt).IsEqualTo("deploy-prod");
        var v2 = s.AddVersion();

        var result = TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));

        await Assert.That(result).IsTypeOf<PipelineActivation.Active>();
        await Assert.That(s.Get(held).Status).IsEqualTo(RunStatus.Cancelled);
        // The block survives the switch (keyed on step name).
        await Assert.That(s.D.GateService.IsBlocked(s.Pipeline.Id, "deploy-prod")).IsTrue();
        await Assert.That(s.D.Continuations.ContinueRun(held.Id, Input(deployTo: "prod"))).Failed();
    }

    [Test]
    public async Task OlderRun_NeverStartsAStepANewerRunAlreadyStarted()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var older = s.Start(Input(deployTo: "prod"), RunTriggerKind.Push);
        s.SucceedThroughDeployBeta(older);
        var newer = s.Start(Input(deployTo: "prod", sha: OtherSha), RunTriggerKind.Push);
        TestRunDomain.Pick(s.D.AttemptService.Schedule(newer.Id, "deploy-prod")); // the newer run got there first

        await Assert.That(s.D.GateService.Unblock(s.Pipeline.Id, "deploy-prod")).Succeeded();

        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Get(older).SupersededBy).IsEqualTo(newer.Id);
        await Assert.That(s.Latest(older, "deploy-prod")).IsNull();
    }

    [Test]
    public async Task ContinuingAnOlderRun_StillRunsAStepANewerRunStartedBefore()
    {
        var s = new SchedulerScenario();
        var older = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(older);
        s.Succeed(older, "summary");
        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Succeeded);
        var newer = s.Start(Input(deployTo: "prod", sha: OtherSha), RunTriggerKind.Push);
        TestRunDomain.Pick(s.D.AttemptService.Schedule(newer.Id, "deploy-prod"));
        s.Succeed(newer, "deploy-prod"); // the newer run already deployed prod

        await Assert.That(s.D.Continuations.ContinueRun(older.Id, Input(deployTo: "prod"))).Succeeded();

        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Latest(older, "deploy-prod")).IsNotNull();
    }

    [Test]
    public async Task AutomaticRunAbandonedBySwitch_StartsAgainOnTheNewVersion()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var push = s.Start(Input(deployTo: "prod"), RunTriggerKind.Push);
        s.SucceedThroughDeployBeta(push);
        var v2 = s.AddVersion();

        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));

        await Assert.That(s.Get(push).Status).IsEqualTo(RunStatus.Cancelled);
        var again = s.D.Runs.List().Single(r => r.Number > push.Number);
        await Assert.That(again.VersionId).IsEqualTo(v2.Id);
        await Assert.That(again.Trigger.Kind).IsEqualTo(RunTriggerKind.Push);
        await Assert.That(again.Input).IsEqualTo(push.Input);
    }

    [Test]
    public async Task AbandonedManualRuns_AndCancelRunningSwitches_AreNotStartedAgain()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var manual = s.Start(Input(deployTo: "prod"));
        s.SucceedThroughDeployBeta(manual);
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, s.AddVersion().Id));
        await Assert.That(s.D.Runs.List().Count()).IsEqualTo(1);

        var push = s.Start(Input(sha: OtherSha), RunTriggerKind.Push);
        s.MarkInProgress(push, "checkout");
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, s.AddVersion().Id, cancelRunning: true));
        await Assert.That(s.D.Runs.List().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task RequestQueuedDuringTheDrain_WinsOverTheAbandonedRun()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input(), RunTriggerKind.Push);
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));
        await Assert.That(s.StartOutcome(Input(sha: OtherSha), RunTriggerKind.Push)).IsTypeOf<RunStartOutcome.Queued>();

        s.Succeed(old, "checkout"); // the drain completes

        var started = s.D.Runs.List().Where(r => r.VersionId == v2.Id).ToList();
        await Assert.That(started.Count).IsEqualTo(1);
        await Assert.That(started[0].Input).Contains(OtherSha);
    }

    [Test]
    public async Task Switch_CancelRunning_CancelsInFlightWorkAndCompletes()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input());
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();

        var result = TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id, cancelRunning: true));

        await Assert.That(result).IsTypeOf<PipelineActivation.Active>();
        await Assert.That(s.Latest(old, "checkout")!.Status).IsTypeOf<Cancelled>();
        await Assert.That(s.Get(old).Status).IsEqualTo(RunStatus.Cancelled);
    }

    [Test]
    public async Task RollbackWhileDraining_StartsQueuedRequestOnOldVersion()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input());
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));
        s.StartOutcome(Input(sha: OtherSha));

        var result = TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, s.Version.Id));

        await Assert.That(result).IsTypeOf<PipelineActivation.Active>();
        var newest = s.D.RunService.ListByPipeline(s.Pipeline.Id)[0];
        await Assert.That(newest.VersionId).IsEqualTo(s.Version.Id);
        await Assert.That(newest.Input).Contains(OtherSha);
        // The in-flight run on the (still active) old version continues normally.
        s.Succeed(old, "checkout");
        await Assert.That(s.Active(old)).IsEquivalentTo(["build", "test"]);
    }
}
