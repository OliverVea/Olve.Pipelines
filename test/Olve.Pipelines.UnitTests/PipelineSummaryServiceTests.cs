using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class PipelineSummaryServiceTests
{
    private sealed record Fixture(
        EntityStore<Pipeline> Pipelines,
        EntityStore<Job> JobStore,
        EntityStore<PipelineConfigBinding> Bindings,
        PipelineSummaryService Service);

    private static Fixture Create()
    {
        var idProvider = new IdProvider();
        var pipelines = new EntityStore<Pipeline>([]);
        var jobStore = new EntityStore<Job>([]);
        var bindings = new EntityStore<PipelineConfigBinding>([]);

        var jobTime = new TimeProviderMake().Instance();
        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, idProvider, jobTime);
        var service = new PipelineSummaryService(
            new PipelineService(pipelines, idProvider), new PipelineConfigBindingService(bindings, idProvider), jobService);
        return new Fixture(pipelines, jobStore, bindings, service);
    }

    private static T Pick<T>(Result<T> r)
    {
        r.TryPickProblems(out _, out var v);
        return v!;
    }

    [Test]
    public async Task NeverRunPipeline_HasNoSteps_AndIsIdle()
    {
        var f = Create();
        await Assert.That(new PipelineService(f.Pipelines, new IdProvider()).Create("p")).Succeeded();

        var summaries = f.Service.List();

        await Assert.That(summaries).Count().IsEqualTo(1);
        await Assert.That(summaries[0].Steps).IsEmpty();
        await Assert.That(summaries[0].Status).IsEqualTo(PipelineStatus.Idle);
        await Assert.That(summaries[0].LastChangedAt).IsNull();
    }

    [Test]
    public async Task StepWithJobs_ReportsLatestStatus()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));

        var baseTime = DateTimeOffset.UtcNow;
        // Older failed job, then a newer done job — the summary must report the newest (done).
        f.JobStore.Set(TestJobs.Make(pipeline.Id, "deploy", baseTime, new Failed(baseTime, baseTime)));
        f.JobStore.Set(TestJobs.Make(pipeline.Id, "deploy", baseTime.AddMinutes(1), new Done(baseTime, baseTime.AddMinutes(2))));

        var summaries = f.Service.List();

        var step = summaries[0].Steps.Single();
        await Assert.That(step.Name).IsEqualTo("deploy");
        await Assert.That(step.Status).IsEqualTo("done");
        await Assert.That(summaries[0].Status).IsEqualTo(PipelineStatus.Healthy);
        await Assert.That(summaries[0].LastChangedAt).IsEqualTo(baseTime.AddMinutes(2));
    }

    [Test]
    public async Task Steps_AreOneBoxPerStepName_OrderedByName()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        f.JobStore.Set(TestJobs.Make(pipeline.Id, "deploy"));
        f.JobStore.Set(TestJobs.Make(pipeline.Id, "build"));
        f.JobStore.Set(TestJobs.Make(pipeline.Id, "build"));

        var names = f.Service.List()[0].Steps.Select(s => s.Name).ToArray();

        await Assert.That(names).IsEquivalentTo(new[] { "build", "deploy" });
    }

    [Test]
    public async Task BoundPipeline_ExposesRepo()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        await Assert.That(new PipelineConfigBindingService(f.Bindings, new IdProvider())
            .Create(pipeline.Id, "OliverVea/Olve.Pipelines", "main", ".pipelines", null)).Succeeded();

        var summaries = f.Service.List();

        await Assert.That(summaries[0].Repo).IsEqualTo("OliverVea/Olve.Pipelines");
    }

    [Test]
    public async Task UnboundPipeline_HasNullRepo()
    {
        var f = Create();
        await Assert.That(new PipelineService(f.Pipelines, new IdProvider()).Create("draft")).Succeeded();

        var summaries = f.Service.List();

        await Assert.That(summaries[0].Repo).IsNull();
    }
}
