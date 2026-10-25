using Olve.Pipelines.Pipelines.Runs;
using Olve.Results;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Runs;

public class CriticalPathTests
{
    private static CriticalPathService Service(SchedulerScenario s) => new(s.D.RunService, s.D.AttemptService, s.D.Plans);

    private static void Ok(Result result)
    {
        if (result.TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
    }

    /// <summary>Plays the executor for one stage: each step's latest attempt starts after its wait and runs for its seconds.</summary>
    private static void Stage(SchedulerScenario s, Run run, params (string Step, double Took, double Wait)[] steps)
    {
        var attempts = steps.ToDictionary(x => x.Step, x => s.Latest(run, x.Step)!);
        var events = steps.SelectMany(x => new[] { (At: x.Wait, Step: x.Step, Start: true), (At: x.Wait + x.Took, Step: x.Step, Start: false) })
            .OrderBy(e => e.At).ThenByDescending(e => e.Start).ToList();
        var now = 0.0;
        foreach (var e in events)
        {
            s.Clock.Advance(TimeSpan.FromSeconds(e.At - now));
            now = e.At;
            var attempt = attempts[e.Step];
            if (e.Start)
                TestRunDomain.Pick(s.D.AttemptService.MarkInProgress(attempt.Id));
            else
                Ok(s.D.AttemptService.MarkSucceeded(attempt.Id, DefaultOutput(s.Get(run).Number, e.Step, attempt.Number)));
        }
    }

    /// <summary>A succeeded beta run (deploy-prod excluded); build and test run in parallel.</summary>
    private static Run Complete(SchedulerScenario s, double build, double test, double testWait = 0)
    {
        var run = s.Start(Input(deployTo: "beta"));
        Stage(s, run, ("checkout", 5, 0));
        Stage(s, run, ("build", build, 0), ("test", test, testWait));
        Stage(s, run, ("deploy-beta", 20, 0));
        Stage(s, run, ("summary", 1, 0));
        return s.Get(run);
    }

    [Test]
    public async Task EachStage_ChargesTheStepThatFinishedLast()
    {
        var s = new SchedulerScenario();
        var run = Complete(s, build: 60, test: 90, testWait: 10);
        await Assert.That(run.Status).IsEqualTo(RunStatus.Succeeded);

        var report = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false));

        var path = report.Runs.Single().Path;
        await Assert.That(path.Select(p => p.Step)).IsEquivalentTo(["checkout", "test", "deploy-beta", "summary"]);
        var test = path.Single(p => p.Step == "test");
        await Assert.That(Math.Round(test.Seconds)).IsEqualTo(100);
        await Assert.That(Math.Round(test.WaitSeconds)).IsEqualTo(10);
        await Assert.That(Math.Round(report.TotalSeconds)).IsEqualTo(126);
        var share = report.Steps.Single(st => st.Step == "test");
        await Assert.That(share.RunNumbers).IsEquivalentTo([run.Number]);
        await Assert.That(Math.Round(share.Share * 100)).IsEqualTo(Math.Round(100.0 * 100 / 126));
    }

    [Test]
    public async Task Steps_ListTheRunsTheyWereOnThePathIn()
    {
        var s = new SchedulerScenario();
        var slowTest = Complete(s, build: 30, test: 90);
        var slowBuild = Complete(s, build: 120, test: 40);

        var report = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, slowTest.Number, slowBuild.Number, includeUnsuccessful: false));

        var byStep = report.Steps.ToDictionary(st => st.Step);
        await Assert.That(byStep["test"].RunNumbers).IsEquivalentTo([slowTest.Number]);
        await Assert.That(byStep["build"].RunNumbers).IsEquivalentTo([slowBuild.Number]);
        await Assert.That(byStep["checkout"].Runs).IsEqualTo(2);
        await Assert.That(report.Steps[0].Step).IsEqualTo("build");
    }

    [Test]
    public async Task ALaterRerun_IsLeftOut_WithANote()
    {
        var s = new SchedulerScenario();
        var run = Complete(s, build: 30, test: 40);
        s.Clock.Advance(TimeSpan.FromHours(1));
        TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "build", continueAfter: false));
        Stage(s, run, ("build", 300, 0));

        var report = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false));

        var reported = report.Runs.Single();
        await Assert.That(reported.Path.Single(p => p.Stage == 1).Step).IsEqualTo("test");
        await Assert.That(Math.Round(reported.Seconds)).IsEqualTo(66);
        await Assert.That(reported.Notes.Single()).Contains("build@2 left out");
    }

    [Test]
    public async Task UnsuccessfulAndRunningRuns_AreSkipped_UnlessAsked()
    {
        var s = new SchedulerScenario();
        var failed = s.Start(Input(deployTo: "beta"));
        Stage(s, failed, ("checkout", 5, 0));
        s.Fail(failed, "build");
        s.Succeed(failed, "test");
        var running = s.Start(Input(deployTo: "beta"));

        var report = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false));
        await Assert.That(report.Runs).IsEmpty();
        await Assert.That(report.Skipped.Select(x => x.Number)).IsEquivalentTo([failed.Number, running.Number]);

        var all = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: true));
        await Assert.That(all.Runs.Single().Number).IsEqualTo(failed.Number);
        await Assert.That(all.Skipped.Single().Reason).IsEqualTo("still running");
    }

    [Test]
    public async Task SinceVersion_StartsAtTheFirstRunOnThatVersion()
    {
        var s = new SchedulerScenario();
        Complete(s, build: 1, test: 1);
        var v2 = s.AddVersion();
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, v2.Id));
        var first = Complete(s, build: 1, test: 1);
        var second = Complete(s, build: 1, test: 1);

        var report = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false, sinceVersion: v2.Number));
        var none = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false, sinceVersion: v2.Number + 1));

        await Assert.That(report.Runs.Select(r => r.Number)).IsEquivalentTo([first.Number, second.Number]);
        await Assert.That(none.Runs).IsEmpty();
    }

    [Test]
    public async Task APushedRun_ReportsItsLead()
    {
        var s = new SchedulerScenario();
        var t = s.Clock.GetUtcNow();
        var origin = new RunOrigin(t.AddSeconds(-10), "webhook", PushedAt: t.AddSeconds(-12), ConfigReadyAt: t.AddSeconds(-9),
            MapStartedAt: t.AddSeconds(-9), MapFinishedAt: t.AddSeconds(-2));
        var run = TestRunDomain.Pick(s.D.Starts.Start(s.Pipeline.Id, Input(deployTo: "beta"), new RunTrigger(RunTriggerKind.Push, Origin: origin)));
        var started = ((Olve.Pipelines.Pipelines.Scheduling.RunStartOutcome.Started)run).Run;
        Stage(s, started, ("checkout", 5, 0));
        Stage(s, started, ("build", 1, 0), ("test", 1, 0));
        Stage(s, started, ("deploy-beta", 1, 0));
        Stage(s, started, ("summary", 1, 0));

        var lead = TestRunDomain.Pick(Service(s).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false)).Runs.Single().Lead!;

        await Assert.That(lead.Via).IsEqualTo("webhook");
        await Assert.That(lead.Detect).IsEqualTo(2);
        await Assert.That(lead.Check).IsEqualTo(1);
        await Assert.That(lead.Map).IsEqualTo(7);
        await Assert.That(Math.Round(lead.Start!.Value)).IsEqualTo(2);
        await Assert.That(Math.Round(lead.Seconds)).IsEqualTo(12);
    }

    [Test]
    public async Task ARange_IsChecked()
    {
        var s = new SchedulerScenario();
        Complete(s, build: 1, test: 1);

        await Assert.That(Service(s).Get(s.Pipeline.Id, 5, 2, includeUnsuccessful: false)).Failed();
        await Assert.That(Service(s).Get(s.Pipeline.Id, 1, 1000, includeUnsuccessful: false)).Failed();
    }
}
