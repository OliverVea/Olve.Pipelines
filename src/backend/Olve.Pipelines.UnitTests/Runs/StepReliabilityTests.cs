using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Runs;

public class StepReliabilityTests
{
    private static ReliabilityReport Report(SchedulerScenario s, int? runs = null)
        => TestRunDomain.Pick(new StepReliabilityService(s.D.RunService, s.D.AttemptService, s.D.Plans, s.D.VersionService).Get(s.Pipeline.Id, runs));

    private static StepReliability Step(ReliabilityReport report, string step) => report.Steps.Single(x => x.Step == step);

    private static string Sha(int i) => i.ToString("x40");

    private static SchedulerScenario WithRetries(string step, int retries)
        => new(Mutated(root => ManifestFixture.Step(root, step)["retries"] = retries));

    /// <summary>Checks out <paramref name="sha"/>: downstream steps' input follows it.</summary>
    private static Run UpToTest(SchedulerScenario s, string? sha)
    {
        var run = s.Start(Input(publishCli: false, sha: sha));
        s.Succeed(run, "checkout", $$"""{"repo":"OliverVea/Olve.Pipelines","sha":"{{sha ?? SchedulerScenario.Sha}}","tree":{{FileRef(run.Number, "checkout", 1, "tree/")}}}""");
        s.Succeed(run, "build");
        return run;
    }

    private static void Finish(SchedulerScenario s, Run run)
    {
        s.Succeed(run, "deploy-beta");
        s.Succeed(run, "summary");
    }

    private static Run Passing(SchedulerScenario s, string? sha = null)
    {
        var run = UpToTest(s, sha);
        s.Succeed(run, "test");
        Finish(s, run);
        return run;
    }

    private static Run Failing(SchedulerScenario s, string? sha = null, string reason = "boom", AttemptFailureKind? kind = null)
    {
        var run = UpToTest(s, sha);
        s.Fail(run, "test", reason, kind);
        return run;
    }

    private static void Rerun(SchedulerScenario s, Run run, string step)
        => TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, step, continueAfter: true));

    [Test]
    public async Task AFailureARerunTurnsIntoASuccess_IsFlaky_AndAFailureForNoReason()
    {
        var s = new SchedulerScenario();
        Passing(s, Sha(1));
        var run = Failing(s, Sha(2));
        Rerun(s, run, "test");
        s.Succeed(run, "test");
        Finish(s, run);
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);

        var report = Report(s);

        var test = Step(report, "test");
        await Assert.That((test.Attempts, test.Failures, test.OwnFailures)).IsEqualTo((3, 1, 1));
        await Assert.That((test.FlakyFailures, test.ReproducedFailures, test.UnverifiedFailures)).IsEqualTo((1, 0, 0));
        await Assert.That(test.FlakyRate).IsEqualTo(1.0 / 3);
        await Assert.That(test.FlakyRuns).IsEquivalentTo([run.Number]);
        await Assert.That(report.Steps[0].Step).IsEqualTo("test");
        await Assert.That(report.Runs).IsEquivalentTo(new ReliabilityRuns(2, 1, 0.5, 0, [run.Number]));
    }

    [Test]
    public async Task AFailureThatRepeatsOnTheSameInput_IsReproduced_AndNoFlake()
    {
        var s = new SchedulerScenario();
        var run = Failing(s);
        Rerun(s, run, "test");
        s.Fail(run, "test");

        var report = Report(s);

        var test = Step(report, "test");
        await Assert.That((test.FlakyFailures, test.ReproducedFailures, test.UnverifiedFailures)).IsEqualTo((0, 2, 0));
        await Assert.That(test.FailureRate).IsEqualTo(1.0);
        await Assert.That(report.Runs.FailedForNoReason).IsEqualTo(0);
    }

    [Test]
    public async Task ADifferentInputOrCode_ProvesNothing()
    {
        var s = new SchedulerScenario();
        Failing(s, Sha(1));
        Passing(s, Sha(2));

        var otherCode = s.AddVersion(Mutated(root => root["codeHash"] = "sha256:" + new string('c', 64)));
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, otherCode.Id));
        Passing(s, Sha(1));

        var report = Report(s);

        var test = Step(report, "test");
        await Assert.That((test.FlakyFailures, test.UnverifiedFailures)).IsEqualTo((0, 1));
        await Assert.That(report.Runs.FailedForNoReason).IsEqualTo(0);
    }

    [Test]
    public async Task TheSameInputInAnotherRun_Matches_ThoughItsFileRefsNameTheirOwnRun()
    {
        var s = new SchedulerScenario();
        var failed = Failing(s);
        var rerun = Passing(s);
        await Assert.That(s.Latest(rerun, "test")!.InputHash).IsNotEqualTo(s.Latest(failed, "test")!.InputHash);

        var test = Step(Report(s), "test");

        await Assert.That((test.FlakyFailures, test.UnverifiedFailures)).IsEqualTo((1, 0));
        await Assert.That(test.FlakyRuns).IsEquivalentTo([failed.Number]);
    }

    [Test]
    [Arguments(AttemptFailureKind.PodFailed, 1, 0)]
    [Arguments(AttemptFailureKind.JobLost, 1, 0)]
    [Arguments(AttemptFailureKind.DeadlineExceeded, 0, 1)]
    public async Task InfrastructureFailures_StayApartFromTheStepsOwn(AttemptFailureKind kind, int infrastructure, int timeouts)
    {
        var s = new SchedulerScenario();
        Failing(s, Sha(1), kind: kind);
        Passing(s, Sha(1));

        var report = Report(s);

        var test = Step(report, "test");
        await Assert.That((test.OwnFailures, test.InfrastructureFailures, test.Timeouts)).IsEqualTo((0, infrastructure, timeouts));
        await Assert.That((test.FlakyFailures, test.UnverifiedFailures)).IsEqualTo((0, 0));
        await Assert.That(report.Runs.FailedForNoReason).IsEqualTo(1);
    }

    [Test]
    public async Task AttemptsRecordedWithoutAKind_AreClassifiedFromTheirReason()
    {
        var s = new SchedulerScenario();
        Failing(s, Sha(1), reason: "container 'step' cannot start: ImagePullBackOff");
        Failing(s, Sha(2), reason: "the handler threw (exit 1): assertion failed");

        var test = Step(Report(s), "test");

        await Assert.That((test.OwnFailures, test.InfrastructureFailures)).IsEqualTo((1, 1));
    }

    [Test]
    public async Task AutomaticRetries_AbsorbAFailure_UntilTheyRunOut()
    {
        var s = WithRetries("build", 1);

        var absorbed = s.Start(Input(publishCli: false, sha: Sha(1)));
        s.Succeed(absorbed, "checkout");
        s.Succeed(absorbed, "test");
        s.Fail(absorbed, "build");
        s.Succeed(absorbed, "build");
        Finish(s, absorbed);

        var stopped = s.Start(Input(publishCli: false, sha: Sha(2)));
        s.Succeed(stopped, "checkout");
        s.Succeed(stopped, "test");
        s.Fail(stopped, "build", kind: AttemptFailureKind.PodFailed);
        s.Fail(stopped, "build", kind: AttemptFailureKind.PodFailed);
        await Assert.That(s.Get(stopped).Status).IsEqualTo(RunStatus.Failed);

        var report = Report(s);

        await Assert.That(Step(report, "build").FlakyFailures).IsEqualTo(1);
        await Assert.That(report.Runs).IsEquivalentTo(new ReliabilityRuns(2, 1, 0.5, 1, [stopped.Number]));
    }

    [Test]
    public async Task ADeclinedStep_IsNoOutcome_AndAnApprovalAfterItNoFlake()
    {
        var s = new SchedulerScenario(Mutated(root =>
        {
            root["models"]!["Approval"] = new JsonObject
            {
                ["output"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["approved"] = new JsonObject { ["type"] = "boolean" } },
                    ["required"] = new JsonArray("approved"),
                    ["additionalProperties"] = false,
                },
            };
            ManifestFixture.Step(root, "deploy-beta")["externalInputs"] = new JsonObject
            {
                ["decision"] = new JsonObject { ["model"] = "Approval", ["title"] = "Deploy?", ["from"] = new JsonArray(new JsonObject { ["kind"] = "person" }) },
            };
        }));
        JsonElement Approve(bool approved) => JsonDocument.Parse($$"""{"approved":{{(approved ? "true" : "false")}}}""").RootElement.Clone();
        var run = UpToTest(s, null);
        s.Succeed(run, "test");
        TestRunDomain.Pick(s.D.Inputs.Decline(s.Pipeline.Id, run.Number, "deploy-beta", "not today", "oliver"));
        Rerun(s, run, "deploy-beta");
        TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Approve(false), "oliver"));
        s.Fail(run, "deploy-beta");
        Rerun(s, run, "deploy-beta");
        TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Approve(true), "oliver"));
        s.Succeed(run, "deploy-beta");

        var deploy = Step(Report(s), "deploy-beta");

        await Assert.That((deploy.Attempts, deploy.OwnFailures)).IsEqualTo((2, 1));
        await Assert.That((deploy.FlakyFailures, deploy.UnverifiedFailures)).IsEqualTo((0, 1));
    }

    [Test]
    public async Task OnlyOutcomesCount_AndOnlySettledRuns()
    {
        var s = new SchedulerScenario();
        var cancelled = UpToTest(s, Sha(1));
        TestRunDomain.Pick(s.D.Cancellations.Cancel(cancelled.Id));
        var running = UpToTest(s, Sha(2));
        s.Fail(running, "test", kind: AttemptFailureKind.PodFailed);
        var skipped = s.Start(Input(sha: Sha(3)));
        await Assert.That(s.D.AttemptService.MarkSkipped(s.Latest(skipped, "checkout")!.Id, "no change")).Succeeded();

        var report = Report(s);

        await Assert.That(report.Steps.Any(x => x.Step == "summary")).IsFalse();
        await Assert.That(Step(report, "checkout").Attempts).IsEqualTo(2);
        await Assert.That(Step(report, "test").Attempts).IsEqualTo(1);
        await Assert.That(report.Runs.Settled).IsEqualTo(s.D.RunService.ListByPipeline(s.Pipeline.Id).Count(r => r.Status is RunStatus.Succeeded or RunStatus.Failed));
    }

    [Test]
    [Arguments("20 pass, then 3 fail in the latest 10", 20, 3, true)]
    [Arguments("20 pass, then 2 fail: too few", 20, 2, false)]
    [Arguments("9 before the latest 10: too little history", 9, 10, false)]
    public async Task FailingMoreThanUsual_ComparesAStepWithItself(string name, int passing, int recentFailures, bool above)
    {
        var s = new SchedulerScenario();
        var sha = 0;
        for (var i = 0; i < passing; i++)
            Passing(s, Sha(++sha));
        for (var i = 0; i < 10 - recentFailures; i++)
            Passing(s, Sha(++sha));
        for (var i = 0; i < recentFailures; i++)
            Failing(s, Sha(++sha));

        var test = Step(Report(s), "test");

        await Assert.That(test.AboveUsual).IsEqualTo(above).Because(name);
        await Assert.That(test.RecentFailureRate is null).IsEqualTo(passing < 10);
    }

    [Test]
    public async Task AStepThatAlwaysFailsALot_IsUsual()
    {
        var s = new SchedulerScenario();
        for (var i = 1; i <= 40; i++)
        {
            if (i % 2 == 0) Failing(s, Sha(i));
            else Passing(s, Sha(i));
        }

        var test = Step(Report(s), "test");

        await Assert.That(test.FailureRate).IsEqualTo(0.5);
        await Assert.That(test.AboveUsual).IsFalse();
        await Assert.That((test.RecentFailureRate, test.UsualFailureRate)).IsEqualTo(((double?)0.5, (double?)0.5));
    }

    [Test]
    public async Task TheWindow_SaysWhichRunsItCovers_AndWhetherRetentionCutItShort()
    {
        var s = new SchedulerScenario();
        var first = Passing(s, Sha(1));
        var second = Passing(s, Sha(2));
        var third = Passing(s, Sha(3));

        var latest = Report(s, 2).Window;
        await Assert.That(latest).IsEquivalentTo(new ReliabilityWindow(second.Number, third.Number, 2, 2, second.CreatedAt, third.CreatedAt, false));

        var everything = Report(s).Window;
        await Assert.That((everything.FromRun, everything.Runs, everything.RequestedRuns, everything.LimitedByRetention))
            .IsEqualTo((first.Number, 3, StepReliabilityService.DefaultRuns, false));

        await Assert.That(s.D.RunService.Delete(first.Id).Succeeded).IsTrue();
        var trimmed = Report(s).Window;
        await Assert.That((trimmed.FromRun, trimmed.Runs, trimmed.LimitedByRetention)).IsEqualTo((second.Number, 2, true));
    }

    [Test]
    public async Task ANewPipeline_HasAnEmptyReport()
    {
        var report = Report(new SchedulerScenario());

        await Assert.That(report.Steps).IsEmpty();
        await Assert.That(report.Runs).IsEquivalentTo(new ReliabilityRuns(0, 0, 0, 0, []));
        await Assert.That(report.Window).IsEquivalentTo(new ReliabilityWindow(0, 0, 0, StepReliabilityService.DefaultRuns, null, null, false));
    }

    [Test]
    [Arguments(0)]
    [Arguments(501)]
    public async Task ARangeOutsideTheLimits_IsRefused(int runs)
    {
        var s = new SchedulerScenario();

        var result = new StepReliabilityService(s.D.RunService, s.D.AttemptService, s.D.Plans, s.D.VersionService).Get(s.Pipeline.Id, runs);

        await Assert.That(result).Failed();
        await Assert.That(result.TryPickProblems(out var problems) ? problems.Single().FormattedMessage : "")
            .IsEqualTo($"runs must be between 1 and 500, not {runs}.");
    }
}
