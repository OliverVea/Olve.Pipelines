using Olve.Results.TUnit;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>Promotion gates (hold/resume) and version switches (draining queue, completion, cancel).</summary>
public class GateAndSwitchTests
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
    public async Task BlockedStep_WhoseWhenFails_IsSkippedNotHeld()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var run = s.Start(Input(deployTo: "beta"));

        s.SucceedThroughDeployBeta(run);

        await Assert.That(s.Get(run).HeldAt).IsNull();
        await Assert.That(IsSchedulerSkip(s.Latest(run, "deploy-prod")!, out _)).IsTrue();
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
        // The gate survives the switch (keyed on step name).
        await Assert.That(s.D.GateService.IsBlocked(s.Pipeline.Id, "deploy-prod")).IsTrue();
        await Assert.That(s.D.Continuations.ContinueRun(held.Id, Input(deployTo: "prod"))).Failed();
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
