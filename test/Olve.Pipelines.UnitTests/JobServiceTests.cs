using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using Rocks;
using static Olve.Pipelines.Jobs.Job;
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
    public async Task CreateProductionJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();

        var result = service.CreateProductionJob(pipelineId);

        await Assert.That(result).SucceededAndValue(v => v.IsTypeOf<ProductionJob>());
        result.TryPickProblems(out _, out var job);
        await Assert.That(job!.Status).IsTypeOf<Scheduled>();
        await Assert.That(job.PipelineId).IsEqualTo(pipelineId);
    }

    [Test]
    public async Task CreateProcessingJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();
        var artifactBundleId = Id.New<ArtifactBundle>();
        var processingStepId = Id.New<ProcessingStep>();

        var result = service.CreateProcessingJob(pipelineId, artifactBundleId, processingStepId);

        await Assert.That(result).SucceededAndValue(v => v.IsTypeOf<ProcessingJob>());
        result.TryPickProblems(out _, out var job);
        var pj = (ProcessingJob)job!;
        await Assert.That(pj.Status).IsTypeOf<Scheduled>();
        await Assert.That(pj.PipelineId).IsEqualTo(pipelineId);
        await Assert.That(pj.ArtifactBundleId).IsEqualTo(artifactBundleId);
        await Assert.That(pj.ProcessingStepId).IsEqualTo(processingStepId);
    }

    [Test]
    public async Task CreateProductionJob_WithDuplicateId_OverwritesExisting()
    {
        var id = Id.New<Job>();

        var expectations = new IdProviderExpectations();
        expectations.Setups.Create<Job>().ReturnValue(id);
        expectations.Setups.Create<Job>().ReturnValue(id);

        var store = new EntityStore<Job>([]);
        var service = CreateService(store: store, idProvider: expectations.Instance());

        var pipelineId1 = Id.New<Pipeline>();
        var pipelineId2 = Id.New<Pipeline>();

        service.CreateProductionJob(pipelineId1);
        service.CreateProductionJob(pipelineId2);

        store.TryGet(id, out var job);
        await Assert.That(job!.PipelineId).IsEqualTo(pipelineId2);
        await Assert.That(store.List()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateProductionJob_WithPrepopulatedStore_AddsNewJob()
    {
        var existingJob = new ProductionJob(Id.New<Job>(), Id.New<Pipeline>(), DateTimeOffset.UtcNow, new Scheduled());
        var store = new EntityStore<Job>([existingJob]);
        var service = CreateService(store: store);

        var result = service.CreateProductionJob(Id.New<Pipeline>());

        await Assert.That(result).SucceededAndValue(v => v.IsTypeOf<ProductionJob>());
        await Assert.That(store.List()).Count().IsEqualTo(2);
    }
}
