using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

public class RunQueryServiceTests
{
    [Test]
    public async Task TryResolve_WithoutAttempt_IsLatestSucceeded_WithAttempt_IsThatAttempt()
    {
        var d = new TestRunDomain();
        var run = d.CreateRun();
        var failed = d.Schedule("deploy-beta", run);
        await Assert.That(d.AttemptService.MarkFailed(failed.Id, "boom")).Succeeded();
        var ok = d.Schedule("deploy-beta", run);
        await Assert.That(d.AttemptService.MarkSucceeded(ok.Id, "{}")).Succeeded();

        await Assert.That(d.Query.TryResolve(run.PipelineId, run.Number, "deploy-beta", null, out var latest)).IsTrue();
        await Assert.That(latest!.Id).IsEqualTo(ok.Id);
        await Assert.That(d.Query.TryResolve(run.PipelineId, run.Number, "deploy-beta", 1, out var first)).IsTrue();
        await Assert.That(first!.Id).IsEqualTo(failed.Id);
        await Assert.That(d.Query.TryResolve(run.PipelineId, run.Number + 1, "deploy-beta", null, out _)).IsFalse();
        await Assert.That(d.Query.TryResolve(run.PipelineId, run.Number, "deploy-beta", 3, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetLatestSucceededAcrossRuns_SkipsNewerFailures()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);
        var run1 = d.CreateRun(version);
        var run2 = d.CreateRun(version);
        var good = d.Schedule("build", run1);
        await Assert.That(d.AttemptService.MarkSucceeded(good.Id)).Succeeded();
        var bad = d.Schedule("build", run2);
        await Assert.That(d.AttemptService.MarkFailed(bad.Id, "x")).Succeeded();

        await Assert.That(d.Query.TryGetLatestSucceededAcrossRuns(version.PipelineId, "build", out var latest)).IsTrue();
        await Assert.That(latest!.Id).IsEqualTo(good.Id);
    }

    [Test]
    public async Task GetStepHistory_FollowsIdentityAcrossVersions_WithStepVersions()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        var v1 = d.AddVersion(p, steps: new Dictionary<string, int> { ["deploy-beta"] = 2 });
        var v2 = d.AddVersion(p, steps: new Dictionary<string, int> { ["deploy-beta"] = 3 });
        var runOnV1 = d.CreateRun(v1);
        var runOnV2 = d.CreateRun(v2);
        d.Schedule("deploy-beta", runOnV1);
        d.Schedule("deploy-beta", runOnV2);
        d.Schedule("other", runOnV2);

        var history = d.Query.GetStepHistory(p, "deploy-beta");

        await Assert.That(history.Select(h => (h.Run.Number, h.StepVersion)).ToArray())
            .IsEquivalentTo(new[] { (2, (int?)3), (1, (int?)2) });
        await Assert.That(d.Query.GetStepHistory(p, "deploy-beta", limit: 1)).Count().IsEqualTo(1);
    }
}

public class RunCascadeCleanupTests
{
    [Test]
    public async Task DeletingPipeline_RemovesVersionsRunsAttemptsAndOperationalState()
    {
        var d = new TestRunDomain().WireEvents();
        var pipeline = d.CreatePipeline();
        var version = d.AddVersion(pipeline.Id);
        TestRunDomain.Pick(d.ActivationService.BeginSwitch(pipeline.Id, version.Id));
        var run = d.CreateRun(version);
        d.Schedule("build", run);
        var survivor = d.Schedule("build"); // another pipeline

        await Assert.That(d.PipelineService.Delete(pipeline.Id).Succeeded).IsTrue();

        await Assert.That(d.Versions.List()).Count().IsEqualTo(1); // the survivor's
        await Assert.That(d.Runs.List()).Count().IsEqualTo(1);
        await Assert.That(d.Attempts.List().Single().Id).IsEqualTo(survivor.Id);
        await Assert.That(d.Activations.TryGet(pipeline.Id, out _)).IsFalse();
        await Assert.That(d.Sequences.TryGet(pipeline.Id, out _)).IsFalse();
    }

    [Test]
    public async Task DeletingRun_CancelsActiveAttemptsBeforeDeletingThem()
    {
        var d = new TestRunDomain().WireEvents();
        var run = d.CreateRun();
        var active = d.Schedule("build", run);
        var done = d.Schedule("test", run);
        await Assert.That(d.AttemptService.MarkSucceeded(done.Id)).Succeeded();

        var cancelledSeen = new List<Olve.Utilities.Ids.Id<Attempt>>();
        d.AttemptEvents.OnUpdated.Subscribe(e =>
        {
            if (e.After.Status is AttemptStatus.Cancelled) cancelledSeen.Add(e.Id);
        });

        await Assert.That(d.RunService.Delete(run.Id).Succeeded).IsTrue();

        await Assert.That(cancelledSeen).IsEquivalentTo(new[] { active.Id });
        await Assert.That(d.Attempts.List()).IsEmpty();
    }
}
