using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class JobObsoletionServiceTests
{
    private static (JobService Service, EntityStore<Job> Store) CreateServices()
    {
        var store = new EntityStore<Job>([]);
        var timeProvider = new TimeProviderMake().Instance();
        var events = new JobEvents();

        store.OnAdded.Subscribe(events.OnAdded.Invoke);

        var jobService = new JobService(
            NullLogger<JobService>.Instance,
            store,
            new IdProvider(),
            timeProvider);

        var obsoletion = new JobObsoletionService(
            jobService,
            NullLogger<JobObsoletionService>.Instance);

        events.OnAdded.Subscribe(obsoletion.HandleJobAdded);

        return (jobService, store);
    }

    private static Job GetJob(EntityStore<Job> store, Id<Job> id)
    {
        store.TryGet(id, out var job);
        return job!;
    }

    private static Job CreateAndGet(JobService service, Func<JobService, Olve.Results.Result<Job>> create)
    {
        var result = create(service);
        result.TryPickProblems(out _, out var job);
        return job!;
    }

    [Test]
    public async Task ProductionJob_SupersedesPendingForSamePipeline()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId));
        var job2 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId));

        var updatedJob1 = GetJob(store, job1.Id);
        await Assert.That(updatedJob1.Status).IsTypeOf<Obsolete>();
        await Assert.That(((Obsolete)updatedJob1.Status).SupersedingJobId).IsEqualTo(job2.Id);
        await Assert.That(job2.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProcessingJob_SupersedesPendingForSameKey()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProcessingStep>();

        var job1 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<ArtifactBundle>(), stepId));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<ArtifactBundle>(), stepId));

        var updatedJob1 = GetJob(store, job1.Id);
        await Assert.That(updatedJob1.Status).IsTypeOf<Obsolete>();
        await Assert.That(((Obsolete)updatedJob1.Status).SupersedingJobId).IsEqualTo(job2.Id);
        await Assert.That(job2.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionJob_DifferentPipelines_NoSuperseding()
    {
        var (service, store) = CreateServices();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(Id.New<Pipeline>()));
        var job2 = CreateAndGet(service, s => s.CreateProductionJob(Id.New<Pipeline>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProcessingJob_SamePipelineDifferentStep_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<ArtifactBundle>(), Id.New<ProcessingStep>()));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<ArtifactBundle>(), Id.New<ProcessingStep>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionAndProcessingJob_SamePipeline_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<ArtifactBundle>(), Id.New<ProcessingStep>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionJob_AfterFirstInProgress_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId));

        service.UpdateJob<ProductionJob>(job1.Id, j => j with { Status = new InProgress(DateTimeOffset.UtcNow) });

        var job2 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<InProgress>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }
}
