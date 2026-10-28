using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests;

public class AttemptServiceTests
{
    [Test]
    public async Task Schedule_NumbersAttemptsPerRunAndStep_AndDenormalisesRun()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);
        d.CreateRun(version);
        var run = d.CreateRun(version);

        var build1 = d.Schedule("build", run);
        var build2 = d.Schedule("build", run);
        var test1 = d.Schedule("test", run);

        await Assert.That(build1.Number).IsEqualTo(1);
        await Assert.That(build2.Number).IsEqualTo(2);
        await Assert.That(test1.Number).IsEqualTo(1);
        await Assert.That(build1.RunNumber).IsEqualTo(2);
        await Assert.That(build1.PipelineId).IsEqualTo(version.PipelineId);
        await Assert.That(build1.Status).IsTypeOf<Scheduled>();
        await Assert.That(d.AttemptService.PeekNextNumber(run.Id, "build")).IsEqualTo(3);
    }

    [Test]
    public async Task Schedule_RejectsUnknownRunAndBlankStep()
    {
        var d = new TestRunDomain();

        await Assert.That(d.AttemptService.Schedule(Id.New<Run>(), "build")).Failed();
        await Assert.That(d.AttemptService.Schedule(d.CreateRun().Id, " ")).Failed();
    }

    [Test]
    public async Task RecordSkipped_CreatesTerminalAttemptWithReason()
    {
        var d = new TestRunDomain();
        var run = d.CreateRun();

        var skipped = TestRunDomain.Pick(d.AttemptService.RecordSkipped(run.Id, "deploy-prod", "when: deployTo ∉ [prod]"));

        await Assert.That(skipped.Status).IsTypeOf<Skipped>();
        await Assert.That(((Skipped)skipped.Status).Reason).IsEqualTo("when: deployTo ∉ [prod]");
        await Assert.That(((Skipped)skipped.Status).StartedAt).IsNull();
        await Assert.That(d.AttemptService.RecordSkipped(run.Id, "x", "")).Failed();
    }

    [Test]
    public async Task Lifecycle_ScheduledToInProgressToSucceeded_RecordsOutput()
    {
        var d = new TestRunDomain();
        var attempt = d.Schedule();

        var startedAt = TestRunDomain.Pick(d.AttemptService.MarkInProgress(attempt.Id));
        await Assert.That(d.AttemptService.MarkSucceeded(attempt.Id, """{"sha":"abc"}""")).Succeeded();

        var after = d.Get(attempt.Id);
        await Assert.That(after.Status).IsTypeOf<Succeeded>();
        await Assert.That(after.StartedAt()).IsEqualTo(startedAt);
        await Assert.That(after.FinishedAt()).IsNotNull();
        await Assert.That(after.Output).IsEqualTo(new AttemptOutput(StorageLayout.AttemptOutputKey(after), """{"sha":"abc"}"""));
    }

    [Test]
    public async Task MarkInProgress_WhenAlreadyInProgress_KeepsStartTime()
    {
        var d = new TestRunDomain();
        var attempt = d.Schedule();

        var first = TestRunDomain.Pick(d.AttemptService.MarkInProgress(attempt.Id));
        var second = TestRunDomain.Pick(d.AttemptService.MarkInProgress(attempt.Id));

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task MarkSucceeded_RejectsOversizedOrMalformedOutput()
    {
        var d = new TestRunDomain();
        var attempt = d.Schedule();
        var huge = "\"" + new string('x', AttemptService.MaxOutputJsonBytes) + "\"";

        await Assert.That(d.AttemptService.MarkSucceeded(attempt.Id, huge)).Failed();
        await Assert.That(d.AttemptService.MarkSucceeded(attempt.Id, "{ nope")).Failed();
        await Assert.That(d.Get(attempt.Id).Status).IsTypeOf<Scheduled>();
    }

    // A K8s Job finishing after the attempt was cancelled must not resurrect it.
    [Test]
    public async Task Transitions_NeverOverwriteATerminalStatus()
    {
        var d = new TestRunDomain();
        var attempt = d.Schedule();
        await Assert.That(d.AttemptService.Cancel(attempt.Id)).Succeeded();

        await Assert.That(d.AttemptService.MarkSucceeded(attempt.Id)).Succeeded();
        await Assert.That(d.AttemptService.MarkFailed(attempt.Id, "late")).Succeeded();
        await Assert.That(d.AttemptService.MarkSkipped(attempt.Id, "late")).Succeeded();

        await Assert.That(d.Get(attempt.Id).Status).IsTypeOf<Cancelled>();
        await Assert.That(d.AttemptService.Cancel(attempt.Id)).Failed();
    }

    [Test]
    public async Task Cancel_InProgress_KeepsStartTime()
    {
        var d = new TestRunDomain();
        var attempt = d.Schedule();
        var startedAt = TestRunDomain.Pick(d.AttemptService.MarkInProgress(attempt.Id));

        await Assert.That(d.AttemptService.Cancel(attempt.Id)).Succeeded();

        var cancelled = (Cancelled)d.Get(attempt.Id).Status;
        await Assert.That(cancelled.StartedAt).IsEqualTo(startedAt);
    }

    [Test]
    public async Task RecordSubmissionFailure_CountsWhileScheduled()
    {
        var d = new TestRunDomain();
        var attempt = d.Schedule();

        await Assert.That(TestRunDomain.Pick(d.AttemptService.RecordSubmissionFailure(attempt.Id))).IsEqualTo(1);
        await Assert.That(TestRunDomain.Pick(d.AttemptService.RecordSubmissionFailure(attempt.Id))).IsEqualTo(2);
        await Assert.That(d.AttemptService.MarkInProgress(attempt.Id)).Succeeded();
        await Assert.That(d.AttemptService.RecordSubmissionFailure(attempt.Id)).Failed();
    }

    [Test]
    public async Task TryGetLatestSucceeded_PicksHighestSucceededAttempt_IgnoringLaterFailures()
    {
        var d = new TestRunDomain();
        var run = d.CreateRun();
        var a1 = d.Schedule("build", run);
        var a2 = d.Schedule("build", run);
        var a3 = d.Schedule("build", run);
        await Assert.That(d.AttemptService.MarkSucceeded(a1.Id)).Succeeded();
        await Assert.That(d.AttemptService.MarkSucceeded(a2.Id)).Succeeded();
        await Assert.That(d.AttemptService.MarkFailed(a3.Id, "boom")).Succeeded();

        await Assert.That(d.AttemptService.TryGetLatestSucceeded(run.Id, "build", out var latest)).IsTrue();
        await Assert.That(latest!.Id).IsEqualTo(a2.Id);
        await Assert.That(d.AttemptService.TryGetLatestSucceeded(run.Id, "test", out _)).IsFalse();
    }

    [Test]
    public async Task ListQueries_ByRun_ForStep_ByStepName_AndActivity()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);
        var run1 = d.CreateRun(version);
        var run2 = d.CreateRun(version);
        var r1Build = d.Schedule("build", run1);
        var r1Test = d.Schedule("test", run1);
        var r2Build1 = d.Schedule("build", run2);
        var r2Build2 = d.Schedule("build", run2);

        await Assert.That(d.AttemptService.ListByRun(run1.Id).Select(a => a.Id)).IsEquivalentTo(new[] { r1Build.Id, r1Test.Id });
        await Assert.That(d.AttemptService.ListForStep(run2.Id, "build").Select(a => a.Number)).IsEquivalentTo(new[] { 1, 2 });
        // History by identity across runs: newest run first, then newest attempt.
        await Assert.That(d.AttemptService.ListByStepName(version.PipelineId, "build").Select(a => a.Id).ToArray())
            .IsEquivalentTo(new[] { r2Build2.Id, r2Build1.Id, r1Build.Id });
        await Assert.That(d.AttemptService.ListByPipeline(version.PipelineId)).Count().IsEqualTo(4);

        await Assert.That(d.AttemptService.HasActiveAttempts(version.PipelineId)).IsTrue();
        foreach (var a in new[] { r1Build, r1Test })
            await Assert.That(d.AttemptService.Cancel(a.Id)).Succeeded();
        await Assert.That(d.AttemptService.HasActiveAttempts(run1.Id)).IsFalse();
        await Assert.That(d.AttemptService.HasActiveAttempts(run2.Id)).IsTrue();
    }

    [Test]
    public async Task ActiveAttemptQuery_ListsOnlyActiveAttempts_AcrossPipelines_ByAddress()
    {
        var d = new TestRunDomain();
        var run = d.CreateRun();
        var a = d.Schedule("a", run);
        d.Schedule("b", run);
        d.Schedule("c"); // other pipeline
        await Assert.That(d.AttemptService.Cancel(a.Id)).Succeeded();

        var active = new ActiveAttemptQuery(d.AttemptService, d.PipelineService).List();

        await Assert.That(active.Select(x => x.Step)).IsEquivalentTo(["b", "c"]);
        await Assert.That(active[0].PipelineName).IsEqualTo("p");
        await Assert.That(active[0].Run).IsEqualTo(run.Number);
        await Assert.That(active[0].Attempt).IsEqualTo(1);
        await Assert.That(active[0].Status).IsEqualTo("scheduled");
    }
}
