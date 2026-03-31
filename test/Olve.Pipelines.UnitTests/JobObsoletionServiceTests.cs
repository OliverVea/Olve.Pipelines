using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sourcing;
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

        var jobService = new JobService(
            NullLogger<JobService>.Instance,
            store,
            new IdProvider(),
            timeProvider);

        _ = new JobObsoletionService(
            jobService,
            NullLogger<JobObsoletionService>.Instance);

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
    public async Task SourcingJob_SupersedesPendingForSamePipeline()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateSourcingJob(pipelineId));
        var job2 = CreateAndGet(service, s => s.CreateSourcingJob(pipelineId));

        var updatedJob1 = GetJob(store, job1.Id);
        await Assert.That(updatedJob1.Status).IsTypeOf<Obsolete>();
        await Assert.That(((Obsolete)updatedJob1.Status).SupersedingJobId).IsEqualTo(job2.Id);
        await Assert.That(job2.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task BuildJob_SupersedesPendingForSamePipeline()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateBuildJob(pipelineId, Id.New<SourceBundle>()));
        var job2 = CreateAndGet(service, s => s.CreateBuildJob(pipelineId, Id.New<SourceBundle>()));

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
    public async Task SourcingJob_DifferentPipelines_NoSuperseding()
    {
        var (service, store) = CreateServices();

        var job1 = CreateAndGet(service, s => s.CreateSourcingJob(Id.New<Pipeline>()));
        var job2 = CreateAndGet(service, s => s.CreateSourcingJob(Id.New<Pipeline>()));

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
    public async Task SourcingAndBuildJob_SamePipeline_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateSourcingJob(pipelineId));
        var job2 = CreateAndGet(service, s => s.CreateBuildJob(pipelineId, Id.New<SourceBundle>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task SourcingJob_AfterFirstInProgress_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateSourcingJob(pipelineId));

        service.UpdateJob<SourcingJob>(job1.Id, j => j with { Status = new InProgress(DateTimeOffset.UtcNow) });

        var job2 = CreateAndGet(service, s => s.CreateSourcingJob(pipelineId));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<InProgress>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }
}
