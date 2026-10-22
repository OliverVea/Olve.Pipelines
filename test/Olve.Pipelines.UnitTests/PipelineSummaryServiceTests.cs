using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

public class PipelineSummaryServiceTests
{
    private sealed record Fixture(TestRunDomain Domain, EntityStore<PipelineConfigBinding> Bindings, PipelineSummaryService Service);

    private static Fixture Create()
    {
        var domain = new TestRunDomain(TimeProvider.System);
        var bindings = new EntityStore<PipelineConfigBinding>([]);
        var service = new PipelineSummaryService(domain.PipelineService, new PipelineConfigBindingService(bindings, new IdProvider()), domain.Health);
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
}
