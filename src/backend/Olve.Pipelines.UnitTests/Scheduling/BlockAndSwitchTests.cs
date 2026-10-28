using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Results.TUnit;
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
    public async Task Switch_CompletesAtOnce_OldRunCarriesOnItsVersion_NewRunsStartOnTheNewOne()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();

        await Assert.That(TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id))).IsTypeOf<PipelineActivation.Active>();

        s.Succeed(old, "checkout");
        await Assert.That(s.Active(old)).IsEquivalentTo(["build", "test"]);
        await Assert.That(s.Get(old).VersionId).IsEqualTo(s.Version.Id);
        var next = ((RunStartOutcome.Started)s.StartOutcome(Input(sha: OtherSha), RunTriggerKind.Push)).Run;
        await Assert.That(next.VersionId).IsEqualTo(v2.Id);
    }

    [Test]
    public async Task Switch_SamePushAgain_StartsOnTheNewVersion_ThenDeduplicatesThere()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));

        // De-dup only looks at the version a new run would pin: the same push runs again on v2.
        var again = ((RunStartOutcome.Started)s.StartOutcome(Input(sha: Sha), RunTriggerKind.Push)).Run;
        await Assert.That(again.VersionId).IsEqualTo(v2.Id);
        await Assert.That(s.StartOutcome(Input(sha: Sha), RunTriggerKind.Poll)).IsTypeOf<RunStartOutcome.Deduplicated>();
    }

    [Test]
    public async Task Switch_HeldRunStaysHeldOnItsVersion_AndResumesOnUnblock()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();
        var held = s.Start(Input(deployTo: "prod"));
        s.SucceedThroughDeployBeta(held);
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, s.AddVersion().Id));

        await Assert.That(s.Get(held).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Get(held).HeldAt).IsEqualTo("deploy-prod");
        await Assert.That(s.D.GateService.Unblock(s.Pipeline.Id, "deploy-prod")).Succeeded();
        await Assert.That(s.Active(held)).IsEquivalentTo(["deploy-prod"]);
    }

    [Test]
    public async Task Rollback_SwitchesBackAtOnce_AndNewRunsStartOnTheOldVersion()
    {
        var s = new SchedulerScenario();
        var v2 = s.AddVersion();
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));

        await Assert.That(TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, s.Version.Id))).IsTypeOf<PipelineActivation.Active>();

        await Assert.That(s.Start(Input()).VersionId).IsEqualTo(s.Version.Id);
    }
}
