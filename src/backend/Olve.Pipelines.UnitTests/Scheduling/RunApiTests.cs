using System.Text;
using System.Text.Json.Nodes;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>The B8 read models and operations over the self-deploy fixture: run views, history, cancel, continue, health.</summary>
public class RunViewTests
{
    [Test]
    public async Task RunDetail_HasThePinnedStageLayout_WithLatestAttemptPerStep()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.Succeed(run, "checkout");
        s.Fail(run, "build", "compile error");

        var detail = TestRunDomain.Pick(s.D.Views.Get(s.Pipeline.Id, run.Number));

        await Assert.That(detail.Run.Number).IsEqualTo(run.Number);
        await Assert.That(detail.Continuable).IsTrue();
        await Assert.That(detail.Input.GetProperty("deployTo").GetString()).IsEqualTo("beta");
        var steps = detail.Stages.SelectMany(st => st.Steps).ToDictionary(x => x.Name);
        await Assert.That(steps["checkout"].Status).IsEqualTo("succeeded");
        await Assert.That(steps["build"].Status).IsEqualTo("failed");
        await Assert.That(steps["build"].Reason).IsEqualTo("compile error");
        await Assert.That(steps["test"].Status).IsEqualTo("scheduled");
        await Assert.That(steps["deploy-beta"].Status).IsEqualTo("pending");
        // deploy-prod sits inside `when deployTo = prod`.
        var prodStage = detail.Stages.Single(st => st.Steps.Any(x => x.Name == "deploy-prod"));
        await Assert.That(prodStage.When.Select(c => c.Field)).Contains("deployTo");
    }

    [Test]
    public async Task RunDetail_MarksAStepHeldAtItsGate_AndBlocked()
    {
        var s = new SchedulerScenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-beta")).Succeeded();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        var detail = TestRunDomain.Pick(s.D.Views.Get(s.Pipeline.Id, run.Number));
        var step = detail.Stages.SelectMany(st => st.Steps).Single(x => x.Name == "deploy-beta");

        await Assert.That(detail.Run.HeldAt).IsEqualTo("deploy-beta");
        await Assert.That(step.Status).IsEqualTo("held");
        await Assert.That(step.Blocked).IsTrue();
    }

    [Test]
    public async Task List_IsNewestFirst_AndPaged()
    {
        var s = new SchedulerScenario();
        var first = s.Start(Input());
        var second = s.Start(Input());
        var third = s.Start(Input());

        var page = s.D.Views.List(s.Pipeline.Id, offset: 0, limit: 2);

        await Assert.That(page.Items.Select(r => r.Number)).IsEquivalentTo([third.Number, second.Number]);
        await Assert.That(page.TotalCount).IsEqualTo(3);
        await Assert.That(s.D.Views.List(s.Pipeline.Id, 2, 2).Items.Single().Number).IsEqualTo(first.Number);
    }

    [Test]
    public async Task History_FollowsTheStepAcrossRuns_WithChangeMarkers()
    {
        var s = new SchedulerScenario();
        // Version 1 has checkout.1; version 2 changes it to checkout.2.
        var v1 = s.D.AddVersion(s.Pipeline.Id, ManifestFixture.Text, hash: "sha256:v1", steps: new Dictionary<string, int> { ["checkout"] = 1 });
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v1.Id));
        var r1 = s.Start(Input());
        s.Succeed(r1, "checkout");
        var v2 = s.D.AddVersion(s.Pipeline.Id, ManifestFixture.Text, hash: "sha256:v2", steps: new Dictionary<string, int> { ["checkout"] = 2 });
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id, cancelRunning: true));
        var r2 = s.Start(Input());
        s.Succeed(r2, "checkout");

        var history = s.D.Views.History(s.Pipeline.Id, "checkout", limit: 10);

        await Assert.That(history.Select(h => h.RunNumber)).IsEquivalentTo([r2.Number, r1.Number]);
        await Assert.That(history[0].StepVersionChanged).IsTrue();
        await Assert.That(history[1].StepVersionChanged).IsFalse();
        await Assert.That(history[0].StepVersion).IsEqualTo(2);
        await Assert.That(history[1].StepVersion).IsEqualTo(1);
    }
}

public class RunCancellationTests
{
    [Test]
    public async Task Cancel_CancelsTheRunAndItsActiveAttempts_IncludingInProgress()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.MarkInProgress(run, "build"); // test stays Scheduled
        var cancellation = new RunCancellationService(s.D.RunService, s.D.AttemptService, s.D.SchedulingLock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RunCancellationService>.Instance);

        var cancelled = TestRunDomain.Pick(cancellation.Cancel(run.Id));

        await Assert.That(cancelled.Status).IsEqualTo(RunStatus.Cancelled);
        await Assert.That(s.Latest(run, "build")!.Status).IsTypeOf<AttemptStatus.Cancelled>();
        await Assert.That(s.Latest(run, "test")!.Status).IsTypeOf<AttemptStatus.Cancelled>();
        await Assert.That(s.Active(run)).IsEmpty();
        await Assert.That(cancellation.Cancel(run.Id)).Failed();
    }

    private static RunCancellationService Cancellation(SchedulerScenario s) => new(s.D.RunService, s.D.AttemptService, s.D.SchedulingLock,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RunCancellationService>.Instance);

    [Test]
    public async Task CancelStep_CancelsOnlyThatAttempt_AndTheRunEndsCancelledAtTheBarrier()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.MarkInProgress(run, "build"); // build and test share a stage

        var cancelled = TestRunDomain.Pick(Cancellation(s).CancelStep(run.Id, "build"));

        await Assert.That(cancelled.Status).IsTypeOf<AttemptStatus.Cancelled>();
        await Assert.That(s.Latest(run, "test")!.Status).IsTypeOf<AttemptStatus.Scheduled>();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);

        s.Succeed(run, "test");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Cancelled);
    }

    [Test]
    public async Task CancelStep_ThenRerunAndContinue_ReopensTheRun()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "test");
        TestRunDomain.Pick(Cancellation(s).CancelStep(run.Id, "build"));
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Cancelled);

        var rerun = TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "build", continueAfter: true));

        await Assert.That(rerun.Number).IsEqualTo(2);
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);
    }

    [Test]
    public async Task CancelStep_FailsWithoutAnActiveAttempt()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");

        await Assert.That(Cancellation(s).CancelStep(run.Id, "checkout")).Failed();
        await Assert.That(Cancellation(s).CancelStep(run.Id, "nope")).Failed();
    }
}

public class RunInputOperationTests
{
    private static RunInputService Inputs(SchedulerScenario s)
        => new(s.D.ActivationService, s.D.VersionService, s.D.Plans, s.D.Starts, s.D.Continuations);

    [Test]
    public async Task Continue_MergesChangedFields_OverTheStoredInput()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        var continued = TestRunDomain.Pick(Inputs(s).Continue(s.Get(run), JsonNode.Parse("""{"deployTo":"prod"}""")));

        var input = JsonNode.Parse(continued.Input)!;
        await Assert.That(input["deployTo"]!.GetValue<string>()).IsEqualTo("prod");
        await Assert.That(input["sha"]!.GetValue<string>()).IsEqualTo(Sha);
        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-prod"]);
    }

    [Test]
    public async Task Continue_WithAnInvalidValue_IsRejectedByTheInputModel()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        await Assert.That(Inputs(s).Continue(s.Get(run), JsonNode.Parse("""{"deployTo":"staging"}"""))).Failed();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task RerunRun_StartsALinkedRunWithTheSameInput()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));

        var outcome = TestRunDomain.Pick(Inputs(s).RerunRun(s.Get(run)));

        var rerun = ((RunStartOutcome.Started)outcome).Run;
        await Assert.That(rerun.RerunOf).IsEqualTo(run.Id);
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(rerun.Input), JsonNode.Parse(run.Input))).IsTrue();
        await Assert.That(s.D.Views.Summarize(rerun).RerunOfNumber).IsEqualTo(run.Number);
    }
}

public class PipelineHealthTests
{
    [Test]
    [Arguments(false, null, PipelineStatus.Idle)]
    [Arguments(false, RunStatus.Succeeded, PipelineStatus.Healthy)]
    [Arguments(false, RunStatus.Failed, PipelineStatus.Unhealthy)]
    [Arguments(true, null, PipelineStatus.RunningHealthy)]
    [Arguments(true, RunStatus.Succeeded, PipelineStatus.RunningHealthy)]
    [Arguments(true, RunStatus.Failed, PipelineStatus.RunningUnhealthy)]
    public async Task Compute(bool running, RunStatus? lastSettled, string expected)
        => await Assert.That(PipelineHealthService.Compute(running, lastSettled)).IsEqualTo(expected);

    [Test]
    public async Task Health_IgnoresCancelledRuns_AndReportsGatesAndActivation()
    {
        var s = new SchedulerScenario();
        var failed = s.Start(Input());
        s.Fail(failed, "checkout");
        var cancelled = s.Start(Input());
        TestRunDomain.Pick(s.D.RunService.Cancel(cancelled.Id));
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "deploy-prod")).Succeeded();

        var health = s.D.Health.Get(s.Pipeline.Id);

        await Assert.That(health.Status).IsEqualTo(PipelineStatus.Unhealthy);
        await Assert.That(health.LastRun!.Number).IsEqualTo(cancelled.Number);
        await Assert.That(health.LastSettledRun!.Number).IsEqualTo(failed.Number);
        await Assert.That(health.BlockedSteps).IsEquivalentTo(["deploy-prod"]);
        await Assert.That(health.Draining).IsFalse();
        await Assert.That(health.Activation).IsNotNull();
    }

    [Test]
    public async Task Health_SucceededRunWithSkippedSteps_IsHealthy()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta")); // deploy-prod and publish-cli are skipped
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(s.D.Health.Get(s.Pipeline.Id).Status).IsEqualTo(PipelineStatus.Healthy);
    }
}

public class AttemptFilePathTests
{
    [Test]
    [Arguments("greeting.txt", "greeting.txt")]
    [Arguments("out/report.json", "out/report.json")]
    [Arguments("chart/", "chart.tar")]
    [Arguments("a/b/", "a/b.tar")]
    public async Task MapsRefPathsToObjectPaths(string path, string expected)
        => await Assert.That(TestRunDomain.Pick(AttemptFileService.ToObjectPath(path))).IsEqualTo(expected);

    [Test]
    [Arguments("")]
    [Arguments("/etc/passwd")]
    [Arguments("../x")]
    [Arguments("a/../b")]
    [Arguments("a//b")]
    [Arguments("./a")]
    [Arguments("a\\b")]
    public async Task RejectsUnsafePaths(string path)
        => await Assert.That(AttemptFileService.ToObjectPath(path)).Failed();
}

public class ManifestValidateTests
{
    [Test]
    public async Task ValidManifest_IsValid_WithAHash()
    {
        var report = ManifestValidation.Validate(Encoding.UTF8.GetBytes(ManifestFixture.Text));

        await Assert.That(report.Valid).IsTrue();
        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(report.ManifestHash).StartsWith("sha256:");
    }

    [Test]
    public async Task MalformedJson_IsReportedAsAnIssue_NotAnError()
    {
        var report = ManifestValidation.Validate("{ not json"u8.ToArray());

        await Assert.That(report.Valid).IsFalse();
        await Assert.That(report.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.InvalidJson);
        await Assert.That(report.ManifestHash).IsNull();
    }

    [Test]
    public async Task ValidatorErrors_CarryCodePathAndStep()
    {
        var broken = Mutated(m => m["stages"]![0]!["steps"]![0]!["image"] = "");

        var report = ManifestValidation.Validate(Encoding.UTF8.GetBytes(broken));

        await Assert.That(report.Valid).IsFalse();
        var issue = report.Errors.First(e => e.Code == ManifestIssueCodes.StepImageEmpty);
        await Assert.That(issue.Severity).IsEqualTo(Olve.Pipelines.Api.ManifestIssueSeverity.Error);
        await Assert.That(issue.Step).IsEqualTo("checkout");
    }
}

public class JobLogNoteTests
{
    private sealed class FakeLive(string? text) : IAttemptLiveLogs
    {
        public Task<string?> TryGetAsync(Attempt attempt, CancellationToken ct) => Task.FromResult(text);
    }

    [Test]
    public async Task ScheduledAttempt_ReturnsANote_AndInProgressReturnsLiveLogs()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        var attempt = s.Latest(run, "checkout")!;

        // S3 is never touched for active attempts.
        var scheduled = await new JobLogService(null!, null!, s.D.AttemptService, new FakeLive("live")).GetLogsAsync(attempt.Id, default);
        await Assert.That(TestRunDomain.Pick(scheduled)).Contains("is scheduled");

        s.MarkInProgress(run, "checkout");
        var live = await new JobLogService(null!, null!, s.D.AttemptService, new FakeLive("live output\n")).GetLogsAsync(attempt.Id, default);
        await Assert.That(TestRunDomain.Pick(live)).IsEqualTo("live output\n");

        var starting = await new JobLogService(null!, null!, s.D.AttemptService, new FakeLive(null)).GetLogsAsync(attempt.Id, default);
        await Assert.That(TestRunDomain.Pick(starting)).Contains("is starting");
    }

    [Test]
    public async Task StepLog_NamesTheLatestAttempt_AndIsIncompleteWhileItRuns()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        var attempt = s.MarkInProgress(run, "checkout");
        var handler = new GetStepLogsHandler(s.D.RunService, s.D.AttemptService,
            new JobLogService(null!, null!, s.D.AttemptService, new FakeLive("cloning\n")));

        var response = await handler.HandleAsync(new Olve.Pipelines.Api.RunStepsLogsRequest(s.Pipeline.Id.Value.Value, run.Number, "checkout", null), default);

        var log = ((Olve.Pipelines.Api.RunStepsLogsResponse.Ok)response).Body;
        await Assert.That(log.Attempt).IsEqualTo(attempt.Number);
        await Assert.That(log.Status).IsEqualTo("in-progress");
        await Assert.That(log.Complete).IsFalse();
        await Assert.That(log.Text).IsEqualTo("cloning\n");
    }

    [Test]
    public async Task StepsOutsideTheRunsWhen_AreExcluded_EvenWithALegacySkipRecord()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        // Runs scheduled before exclusion recorded a skipped attempt for the when; it still reads as excluded.
        TestRunDomain.Pick(s.D.AttemptService.RecordSkipped(run.Id, "publish-cli", "when deployTo ∉ [prod]"));

        var steps = TestRunDomain.Pick(s.D.Views.Get(s.Pipeline.Id, run.Number)).Stages.SelectMany(st => st.Steps).ToDictionary(x => x.Name);

        foreach (var name in new[] { "deploy-prod", "publish-cli" })
        {
            await Assert.That(steps[name].Status).IsEqualTo("excluded");
            await Assert.That(steps[name].Reason).IsEqualTo("when deployTo ∉ [prod]");
            await Assert.That(steps[name].AttemptCount).IsEqualTo(0);
        }
        await Assert.That(steps["deploy-beta"].Status).IsEqualTo("succeeded");
    }

    [Test]
    public async Task StepHistory_LeavesOutRunsTheStepWasExcludedFrom()
    {
        var s = new SchedulerScenario();
        var beta = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(beta);
        TestRunDomain.Pick(s.D.AttemptService.RecordSkipped(beta.Id, "deploy-prod", "when deployTo ∉ [prod]"));
        var prod = s.Start(Input(deployTo: "prod"));
        s.SucceedThroughDeployBeta(prod);
        s.Succeed(prod, "deploy-prod");

        var history = s.D.Views.History(s.Pipeline.Id, "deploy-prod", limit: 10);

        await Assert.That(history.Select(h => h.RunNumber)).IsEquivalentTo([prod.Number]);
    }

    [Test]
    public async Task Rerun_OfAnExcludedStep_IsRefused()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));
        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "summary");

        var rerun = s.D.Continuations.RerunStep(run.Id, "deploy-prod", continueAfter: false);

        await Assert.That(rerun).Failed();
        await Assert.That(s.Latest(run, "deploy-prod")).IsNull();
    }
}
