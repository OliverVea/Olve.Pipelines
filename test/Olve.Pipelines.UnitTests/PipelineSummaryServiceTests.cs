using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests;

public class PipelineSummaryServiceTests
{
    private sealed record Fixture(
        EntityStore<Pipeline> Pipelines,
        EntityStore<Attempt> JobStore,
        EntityStore<PipelineConfigBinding> Bindings,
        PipelineSummaryService Service);

    private static Fixture Create()
    {
        var idProvider = new IdProvider();
        var pipelines = new EntityStore<Pipeline>([]);
        var jobStore = new EntityStore<Attempt>([]);
        var bindings = new EntityStore<PipelineConfigBinding>([]);

        var attempts = new AttemptService(jobStore, new EntityStore<Run>([]), idProvider, TimeProvider.System);
        var service = new PipelineSummaryService(
            new PipelineService(pipelines, idProvider), new PipelineConfigBindingService(bindings, idProvider), attempts);
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
        // Failed in run 1, then succeeded in run 2 — the summary must report the newest (succeeded).
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "deploy", baseTime, new Failed(baseTime, baseTime), runNumber: 1));
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "deploy", baseTime.AddMinutes(1), new Succeeded(baseTime, baseTime.AddMinutes(2)), runNumber: 2));

        var summaries = f.Service.List();

        var step = summaries[0].Steps.Single();
        await Assert.That(step.Name).IsEqualTo("deploy");
        await Assert.That(step.Status).IsEqualTo("succeeded");
        await Assert.That(summaries[0].Status).IsEqualTo(PipelineStatus.Healthy);
        await Assert.That(summaries[0].LastChangedAt).IsEqualTo(baseTime.AddMinutes(2));
    }

    [Test]
    public async Task Steps_AreOneBoxPerStepName_OrderedByName()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "deploy"));
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "build", number: 1));
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "build", number: 2));

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

    [Test]
    public async Task LatestAttempt_IsOrderedByRunThenAttemptNumber_NotCreationTime()
    {
        var f = Create();
        var pipeline = Pick(new PipelineService(f.Pipelines, new IdProvider()).Create("p"));
        var t = DateTimeOffset.UtcNow;

        // A rerun (attempt 2) of run 1 created after run 2's attempt — run 2 is still the latest.
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "build", t, new Succeeded(t, t), runNumber: 2));
        f.JobStore.Set(TestAttempts.Make(pipeline.Id, "build", t.AddMinutes(1), new Failed(t, t), runNumber: 1, number: 2));

        var step = f.Service.List()[0].Steps.Single();

        await Assert.That(step.Status).IsEqualTo("succeeded");
    }
}
