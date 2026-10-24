using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

public class PipelineSummaryServiceTests
{
    private sealed record Fixture(TestRunDomain Domain, EntityStore<PipelineConfigBinding> Bindings, PipelineSummaryService Service);

    private static Fixture Create()
    {
        var domain = new TestRunDomain(TimeProvider.System);
        var bindings = new EntityStore<PipelineConfigBinding>([]);
        var service = new PipelineSummaryService(domain.PipelineService, new PipelineConfigBindingService(bindings, new IdProvider()), domain.Health,
            domain.ActivationService, domain.Plans, domain.Views, domain.ConfigSnapshots);
        return new Fixture(domain, bindings, service);
    }

    [Test]
    public async Task NeverRunPipeline_IsIdle_WithNoLastRun()
    {
        var f = Create();
        f.Domain.CreatePipeline("p");

        var summary = f.Service.List().Single();

        await Assert.That(summary.Name).IsEqualTo("p");
        await Assert.That(summary.Health.Status).IsEqualTo(PipelineStatus.Idle);
        await Assert.That(summary.Health.LastRun).IsNull();
    }

    [Test]
    public async Task RunningPipeline_ReportsItsLastRun()
    {
        var f = Create();
        var run = f.Domain.CreateRun();

        var summary = f.Service.List().Single(s => s.Id == run.PipelineId);

        await Assert.That(summary.Health.Status).IsEqualTo(PipelineStatus.RunningHealthy);
        await Assert.That(summary.Health.RunningRuns).IsEqualTo(1);
        await Assert.That(summary.Health.LastRun!.Number).IsEqualTo(run.Number);
    }

    [Test]
    public async Task BoundPipeline_ExposesRepo()
    {
        var f = Create();
        var pipeline = f.Domain.CreatePipeline("p");
        await Assert.That(new PipelineConfigBindingService(f.Bindings, new IdProvider())
            .Create(pipeline.Id, "OliverVea/Olve.Pipelines", "main", ".pipelines", null)).Succeeded();

        await Assert.That(f.Service.List().Single().Repo).IsEqualTo("OliverVea/Olve.Pipelines");
    }

    [Test]
    public async Task UnboundPipeline_HasNullRepo()
    {
        var f = Create();
        f.Domain.CreatePipeline("draft");

        await Assert.That(f.Service.List().Single().Repo).IsNull();
    }

    [Test]
    public async Task PipelineWithoutVersion_HasNoStages()
    {
        var f = Create();
        f.Domain.CreatePipeline("p");

        await Assert.That(f.Service.List().Single().Stages).IsEmpty();
    }

    [Test]
    public async Task ActiveVersion_GivesStagesInOrder_WithEachStepsLatestAttempt()
    {
        var f = Create();
        var pipeline = f.Domain.CreatePipeline("p");
        var version = f.Domain.AddVersion(pipeline.Id, TestFixtureManifest.Text);
        TestRunDomain.Pick(f.Domain.ActivationService.BeginSwitch(pipeline.Id, version.Id));
        var run = f.Domain.CreateRun(version, """{"mode":"normal"}""");
        var hello = f.Domain.Schedule("hello", run);
        await Assert.That(f.Domain.AttemptService.MarkSucceeded(hello.Id)).Succeeded();
        f.Domain.Schedule("count", run);

        var stages = f.Service.List().Single().Stages;

        await Assert.That(stages.Select(s => string.Join(",", s.Steps.Select(x => x.Name))))
            .IsEquivalentTo(["hello", "count,echo", "last"]);
        await Assert.That(stages[0].Steps[0].Status).IsEqualTo("succeeded");
        await Assert.That(stages[0].Steps[0].RunNumber).IsEqualTo(run.Number);
        await Assert.That(stages[1].Steps[0].Status).IsEqualTo("scheduled");
        await Assert.That(stages[1].Steps[1].Status).IsNull();
        await Assert.That(stages[2].When.Single().Field).IsEqualTo("mode");
        await Assert.That(stages[0].When).IsEmpty();
    }
}
