using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>Step reruns (only / and continue) and continuing a run with a changed input.</summary>
public class RunContinuationTests
{
    private static (SchedulerScenario S, Run Run) FailedAtDeployBeta()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");
        s.Fail(run, "deploy-beta");
        return (s, run);
    }

    [Test]
    public async Task RerunOnly_OnFailedRun_AddsAttempt_RunStaysFailed_NothingDownstream()
    {
        var (s, run) = FailedAtDeployBeta();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);

        var attempt = TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "deploy-beta", continueAfter: false));

        await Assert.That(attempt.Number).IsEqualTo(2);
        await Assert.That(attempt.Input).IsEqualTo(s.All(run, "deploy-beta")[0].Input); // same pool input
        s.Succeed(run, "deploy-beta");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
        await Assert.That(s.Latest(run, "summary")).IsNull();
        await Assert.That(s.Latest(run, "deploy-prod")).IsNull();
    }

    [Test]
    public async Task RerunAndContinue_OnFailedRun_ReopensAndFinishes()
    {
        var (s, run) = FailedAtDeployBeta();

        TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "deploy-beta", continueAfter: true));
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Get(run).FinishedAt).IsNull();

        s.Succeed(run, "deploy-beta");
        await Assert.That(s.Latest(run, "deploy-prod")).IsNull();
        s.Succeed(run, "summary");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
        // Completed stages were kept.
        await Assert.That(s.All(run, "checkout")).Count().IsEqualTo(1);
        await Assert.That(s.All(run, "build")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Rerun_OfFailedStepWhileRunIsAtTheBarrier_IsPickedUp()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Fail(run, "build"); // test still running → run still Running

        TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "build", continueAfter: false));
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-beta"]);
        // deploy-beta consumes the latest successful build attempt.
        var input = JsonNode.Parse(s.Latest(run, "deploy-beta")!.Input!)!;
        await Assert.That((int)input["artifact"]!["image"]!["attempt"]!).IsEqualTo(2);
    }

    [Test]
    public async Task RerunAndContinue_OfUpstreamStep_RerunsOnlyStepsWhoseInputChanged()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);

        TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "build", continueAfter: true));
        s.Succeed(run, "build"); // new file refs (attempt 2) → deploy-beta's input changes

        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-beta"]);
        await Assert.That(s.Latest(run, "deploy-beta")!.Number).IsEqualTo(2);
        s.Succeed(run, "deploy-beta"); // new version → summary's input changes
        await Assert.That(s.Active(run)).IsEquivalentTo(["summary"]);
        s.Succeed(run, "summary");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(s.All(run, "checkout")).Count().IsEqualTo(1);
        await Assert.That(s.All(run, "test")).Count().IsEqualTo(1);
        // Steps outside the run's when stay excluded: no attempts.
        await Assert.That(s.All(run, "deploy-prod")).Count().IsEqualTo(0);
    }

    [Test]
    public async Task RerunAndContinue_UnchangedOutput_DoesNotRerunDownstream()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "test", continueAfter: true));
        s.Succeed(run, "test"); // identical output; nothing consumes test anyway

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(s.All(run, "deploy-beta")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Rerun_IsRejected_WhileRunIsRunningAndStepDidNotFail()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");

        var rerunSucceeded = s.D.Continuations.RerunStep(run.Id, "checkout", continueAfter: true);
        var rerunActive = s.D.Continuations.RerunStep(run.Id, "build", continueAfter: false);
        var unknown = s.D.Continuations.RerunStep(run.Id, "nope", continueAfter: false);

        await Assert.That(rerunSucceeded).Failed();
        await Assert.That(rerunActive).Failed();
        await Assert.That(unknown).Failed();
    }

    [Test]
    public async Task Rerun_OfStepThatWouldBeSkipped_IsRejected()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        var result = s.D.Continuations.RerunStep(run.Id, "deploy-prod", continueAfter: true);

        await Assert.That(result).Failed();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task Continue_BetaToProd_KeepsCompletedStages_AndPromotes()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        var continued = TestRunDomain.Pick(s.D.Continuations.ContinueRun(run.Id, Input(deployTo: "prod")));

        await Assert.That(continued.Status).IsEqualTo(RunStatus.Running);
        await Assert.That(JsonNode.Parse(continued.Input)!["deployTo"]!.GetValue<string>()).IsEqualTo("prod");
        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-prod"]);
        await Assert.That(s.Latest(run, "deploy-prod")!.Number).IsEqualTo(1);

        s.Succeed(run, "deploy-prod");
        s.Succeed(run, "publish-cli");
        var summaryInput = JsonNode.Parse(s.Latest(run, "summary")!.Input!)!.AsObject();
        await Assert.That(summaryInput.Select(kv => kv.Key).Order().ToList()).IsEquivalentTo(["beta", "cli", "prod"]);
        s.Succeed(run, "summary");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
        foreach (var step in new[] { "checkout", "build", "test", "deploy-beta" })
            await Assert.That(s.All(run, step)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Continue_ChangingAFieldACompletedStepConsumed_IsRejected()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        var result = s.D.Continuations.ContinueRun(run.Id, Input(deployTo: "prod", sha: OtherSha));

        await Assert.That(result).Failed();
        await Assert.That(result.TryPickProblems(out var problems)).IsTrue();
        await Assert.That(string.Join(" ", problems!.Select(p => p.ToString()))).Contains("checkout");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(s.Get(run).Input).IsEqualTo(Input(deployTo: "beta"));
    }

    [Test]
    public async Task Continue_ChangingAWhenFieldOfACompletedStep_IsRejected()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "prod"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "deploy-prod");
        s.Succeed(run, "publish-cli");
        s.Succeed(run, "summary");

        var result = s.D.Continuations.ContinueRun(run.Id, Input(deployTo: "beta"));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Continue_OnFailedRun_RetriesTheFailedStep()
    {
        var (s, run) = FailedAtDeployBeta();

        TestRunDomain.Pick(s.D.Continuations.ContinueRun(run.Id, Input(deployTo: "beta")));

        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-beta"]);
        await Assert.That(s.Latest(run, "deploy-beta")!.Number).IsEqualTo(2);
    }

    [Test]
    public async Task Continue_SupersededRun_IsRejected()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(sha: Sha), RunTriggerKind.Push);
        s.Start(Input(sha: OtherSha), RunTriggerKind.Push);

        await Assert.That(s.D.Continuations.ContinueRun(run.Id, Input(sha: Sha))).Failed();
        await Assert.That(s.D.Continuations.RerunStep(run.Id, "checkout", continueAfter: true)).Failed();
    }
}
