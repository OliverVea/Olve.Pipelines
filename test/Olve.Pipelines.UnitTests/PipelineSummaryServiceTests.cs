using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class PipelineSummaryServiceTests
{
    private sealed record Fixture(
        EntityStore<Pipeline> Pipelines,
        EntityStore<ProductionStep> ProductionStore,
        EntityStore<ProcessingStep> ProcessingStore,
        EntityStore<Job> JobStore,
        EntityStore<PipelineConfigBinding> Bindings,
        ProductionStepService ProductionSteps,
        ProcessingStepService ProcessingSteps,
        PipelineSummaryService Service);

    private static Fixture Create()
    {
        var idProvider = new IdProvider();
        var pipelines = new EntityStore<Pipeline>([]);
        var productionStore = new EntityStore<ProductionStep>([]);
        var processingStore = new EntityStore<ProcessingStep>([]);
        var jobStore = new EntityStore<Job>([]);
        var bindings = new EntityStore<PipelineConfigBinding>([]);

        var pipelineService = new PipelineService(pipelines, idProvider);
        var production = new ProductionStepService(
            productionStore, new AttachmentStore<ProductionStep, StepConfiguration>(productionStore), idProvider);
        var processing = new ProcessingStepService(
            processingStore, new AttachmentStore<ProcessingStep, StepConfiguration>(processingStore), idProvider);
        var bindingService = new PipelineConfigBindingService(bindings, idProvider);
        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, idProvider, new TimeProviderMake().Instance());

        var service = new PipelineSummaryService(pipelineService, production, processing, bindingService, jobService);
        return new Fixture(pipelines, productionStore, processingStore, jobStore, bindings, production, processing, service);
    }

    private static T Pick<T>(Result<T> r)
    {
        r.TryPickProblems(out _, out var v);
        return v!;
    }

    [Test]
    public async Task NeverRunStep_ReportsIdle()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        f.ProductionSteps.Create(pipeline.Id, "build");

        var summaries = f.Service.List();

        await Assert.That(summaries).Count().IsEqualTo(1);
        await Assert.That(summaries[0].Steps).Count().IsEqualTo(1);
        await Assert.That(summaries[0].Steps[0].Status).IsEqualTo("idle");
        await Assert.That(summaries[0].Steps[0].Kind).IsEqualTo("production");
    }

    [Test]
    public async Task StepWithJob_ReportsLatestStatus()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        var step = Pick(f.ProcessingSteps.Create(pipeline.Id, "deploy", 0));

        var baseTime = DateTimeOffset.UtcNow;
        // Older failed job, then a newer done job — the summary must report the newest (done).
        f.JobStore.Set(new ProcessingJob(Id.New<Job>(), pipeline.Id, baseTime, new Failed(baseTime, baseTime),
            Id.New<JobGroup>(), Id.New<ArtifactBundle>(), step.Id));
        f.JobStore.Set(new ProcessingJob(Id.New<Job>(), pipeline.Id, baseTime.AddMinutes(1), new Done(baseTime, baseTime),
            Id.New<JobGroup>(), Id.New<ArtifactBundle>(), step.Id));

        var summaries = f.Service.List();

        var processing = summaries[0].Steps.Single(s => s.Kind == "processing");
        await Assert.That(processing.Status).IsEqualTo("done");
    }

    [Test]
    public async Task Order_ProductionThenProcessing()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        f.ProductionSteps.Create(pipeline.Id, "build");
        f.ProcessingSteps.Create(pipeline.Id, "deploy", 0);

        var summaries = f.Service.List();
        var kinds = summaries[0].Steps.Select(s => s.Kind).ToArray();

        await Assert.That(kinds).IsEquivalentTo(new[] { "production", "processing" });
    }

    [Test]
    public async Task BoundPipeline_ExposesRepo()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        new PipelineConfigBindingService(f.Bindings, new IdProvider())
            .Create(pipeline.Id, "OliverVea/Olve.Pipelines", "main", ".pipelines", null);

        var summaries = f.Service.List();

        await Assert.That(summaries[0].Repo).IsEqualTo("OliverVea/Olve.Pipelines");
    }

    [Test]
    public async Task UnboundPipeline_HasNullRepo()
    {
        var f = Create();
        new PipelineService(f.Pipelines, new IdProvider()).Create("draft");

        var summaries = f.Service.List();

        await Assert.That(summaries[0].Repo).IsNull();
    }
}
