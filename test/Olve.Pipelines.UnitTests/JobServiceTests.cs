using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
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
        var idp = idProvider ?? new IdProvider();
        var jobGroups = new JobGroupService(new EntityStore<JobGroup>([]), idp, timeProvider);

        return new JobService(
            NullLogger<JobService>.Instance,
            store,
            jobGroups,
            idp,
            timeProvider);
    }

    [Test]
    public async Task CreateProductionJob_ReturnsScheduledJob()
    {
        var service = CreateService();
        var pipelineId = Id.New<Pipeline>();
        var jobGroupId = Id.New<JobGroup>();
        var stepId = Id.New<ProductionStep>();

        var result = service.CreateProductionJob(pipelineId, jobGroupId, stepId);

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
        var jobGroupId = Id.New<JobGroup>();
        var artifactBundleId = Id.New<ArtifactBundle>();
        var processingStepId = Id.New<ProcessingStep>();

        var result = service.CreateProcessingJob(pipelineId, jobGroupId, artifactBundleId, processingStepId);

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

        service.CreateProductionJob(pipelineId1, Id.New<JobGroup>(), Id.New<ProductionStep>());
        service.CreateProductionJob(pipelineId2, Id.New<JobGroup>(), Id.New<ProductionStep>());

        store.TryGet(id, out var job);
        await Assert.That(job!.PipelineId).IsEqualTo(pipelineId2);
        await Assert.That(store.List()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateProductionJob_WithPrepopulatedStore_AddsNewJob()
    {
        var existingJob = new ProductionJob(Id.New<Job>(), Id.New<Pipeline>(), DateTimeOffset.UtcNow, new Scheduled(), Id.New<JobGroup>(), Id.New<ProductionStep>());
        var store = new EntityStore<Job>([existingJob]);
        var service = CreateService(store: store);

        var result = service.CreateProductionJob(Id.New<Pipeline>(), Id.New<JobGroup>(), Id.New<ProductionStep>());

        await Assert.That(result).SucceededAndValue(v => v.IsTypeOf<ProductionJob>());
        await Assert.That(store.List()).Count().IsEqualTo(2);
    }

    private static ProcessingJob MakeProcessingJob(
        Id<Pipeline> pipelineId, Id<ProcessingStep> stepId, Id<ArtifactBundle> bundleId, DateTimeOffset createdAt)
        => new(Id.New<Job>(), pipelineId, createdAt, new Scheduled(), Id.New<JobGroup>(), bundleId, stepId);

    [Test]
    public async Task GetLastPromotedBundle_NoRuns_ReturnsNull()
    {
        var service = CreateService();
        var result = service.GetLastPromotedBundle(Id.New<Pipeline>(), Id.New<ProcessingStep>());
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetLastPromotedBundle_ReturnsBundleOfNewestJobForStep()
    {
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProcessingStep>();
        var oldBundle = Id.New<ArtifactBundle>();
        var newBundle = Id.New<ArtifactBundle>();
        var baseTime = DateTimeOffset.UtcNow;

        var store = new EntityStore<Job>([
            MakeProcessingJob(pipelineId, stepId, oldBundle, baseTime),
            MakeProcessingJob(pipelineId, stepId, newBundle, baseTime.AddMinutes(5)),
        ]);
        var service = CreateService(store: store);

        var result = service.GetLastPromotedBundle(pipelineId, stepId);

        await Assert.That(result).IsEqualTo(newBundle);
    }

    [Test]
    public async Task GetLastPromotedBundle_IgnoresOtherStepsAndPipelines()
    {
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProcessingStep>();
        var wantedBundle = Id.New<ArtifactBundle>();
        var baseTime = DateTimeOffset.UtcNow;

        var store = new EntityStore<Job>([
            MakeProcessingJob(pipelineId, stepId, wantedBundle, baseTime),
            // Newer job but for a different step — must be ignored.
            MakeProcessingJob(pipelineId, Id.New<ProcessingStep>(), Id.New<ArtifactBundle>(), baseTime.AddMinutes(10)),
            // Newer job for the same step Id but a different pipeline — must be ignored.
            MakeProcessingJob(Id.New<Pipeline>(), stepId, Id.New<ArtifactBundle>(), baseTime.AddMinutes(10)),
        ]);
        var service = CreateService(store: store);

        var result = service.GetLastPromotedBundle(pipelineId, stepId);

        await Assert.That(result).IsEqualTo(wantedBundle);
    }
}
