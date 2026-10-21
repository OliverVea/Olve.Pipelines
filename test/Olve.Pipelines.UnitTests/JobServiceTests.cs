using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using Rocks;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

[RockPartial(typeof(TimeProvider), BuildType.Make)]
internal sealed partial class TimeProviderMake;

[RockPartial(typeof(IdProvider), BuildType.Create)]
internal sealed partial class IdProviderExpectations;

public class JobServiceTests
{
    private static JobService CreateService(
        EntityStore<Job>? store = null,
        IdProvider? idProvider = null)
    {
        store ??= new EntityStore<Job>([]);
        var timeProvider = new TimeProviderMake().Instance();

        return new JobService(
            NullLogger<JobService>.Instance,
            store,
            idProvider ?? new IdProvider(),
            timeProvider);
    }

    [Test]
    public async Task CreateJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();

        var result = service.CreateJob(pipelineId, "build", "alpine:latest", "echo hi");

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var job);
        await Assert.That(job!.Status).IsTypeOf<Scheduled>();
        await Assert.That(job.PipelineId).IsEqualTo(pipelineId);
        await Assert.That(job.StepName).IsEqualTo("build");
        await Assert.That(job.Key).IsEqualTo(new JobKey(pipelineId, "build"));
    }

    [Test]
    public async Task CreateJob_WithoutStepName_Fails()
    {
        var service = CreateService();

        var result = service.CreateJob(Id.New<Pipeline>(), " ", "alpine:latest", "echo hi");

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task CreateJob_WithDuplicateId_OverwritesExisting()
    {
        var id = Id.New<Job>();

        var expectations = new IdProviderExpectations();
        expectations.Setups.Create<Job>().ReturnValue(id);
        expectations.Setups.Create<Job>().ReturnValue(id);

        var store = new EntityStore<Job>([]);
        var service = CreateService(store: store, idProvider: expectations.Instance());

        var pipelineId1 = Id.New<Pipeline>();
        var pipelineId2 = Id.New<Pipeline>();

        await Assert.That(service.CreateJob(pipelineId1, "build", "img", "sh")).Succeeded();
        await Assert.That(service.CreateJob(pipelineId2, "build", "img", "sh")).Succeeded();

        store.TryGet(id, out var job);
        await Assert.That(job!.PipelineId).IsEqualTo(pipelineId2);
        await Assert.That(store.List()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateJob_WithPrepopulatedStore_AddsNewJob()
    {
        var store = new EntityStore<Job>([TestJobs.Make()]);
        var service = CreateService(store: store);

        var result = service.CreateJob(Id.New<Pipeline>(), "build", "img", "sh");

        await Assert.That(result).Succeeded();
        await Assert.That(store.List()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task GetJobsByPipeline_ReturnsOnlyThatPipelinesJobs()
    {
        var pipelineId = Id.New<Pipeline>();
        var mine = TestJobs.Make(pipelineId);

        var store = new EntityStore<Job>([mine, TestJobs.Make()]);
        var service = CreateService(store: store);

        var result = service.GetJobsByPipeline(pipelineId);

        await Assert.That(result.Select(j => j.Id)).IsEquivalentTo([mine.Id]);
    }

    [Test]
    public async Task CancelJob_Scheduled_BecomesCancelled()
    {
        var job = TestJobs.Make();
        var store = new EntityStore<Job>([job]);
        var service = CreateService(store: store);

        await Assert.That(service.CancelJob(job.Id)).Succeeded();

        store.TryGet(job.Id, out var cancelled);
        await Assert.That(cancelled!.Status).IsTypeOf<Cancelled>();
    }
}
