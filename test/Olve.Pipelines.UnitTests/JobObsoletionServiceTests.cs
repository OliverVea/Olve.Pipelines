using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
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
        // Advancing clock so each job's CreatedAt is distinct and increasing — makes latest-wins
        // supersession deterministic (the Make mock returns a constant timestamp).
        var timeProvider = new MonotonicTimeProvider();
        var events = new JobEvents();

        store.OnAdded.Subscribe(events.OnAdded.Invoke);

        var jobService = new JobService(
            NullLogger<JobService>.Instance,
            store,
            new JobGroupService(new EntityStore<JobGroup>([]), new IdProvider(), timeProvider),
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
    public async Task ProductionJob_SupersedesPendingForSameKey()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProductionStep>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId));
        var job2 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId));

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

        var job1 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), Id.New<ArtifactBundle>(), stepId));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), Id.New<ArtifactBundle>(), stepId));

        var updatedJob1 = GetJob(store, job1.Id);
        await Assert.That(updatedJob1.Status).IsTypeOf<Obsolete>();
        await Assert.That(((Obsolete)updatedJob1.Status).SupersedingJobId).IsEqualTo(job2.Id);
        await Assert.That(job2.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionJob_DifferentPipelines_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var stepId = Id.New<ProductionStep>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(Id.New<Pipeline>(), Id.New<JobGroup>(), stepId));
        var job2 = CreateAndGet(service, s => s.CreateProductionJob(Id.New<Pipeline>(), Id.New<JobGroup>(), stepId));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProcessingJob_SamePipelineDifferentStep_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), Id.New<ArtifactBundle>(), Id.New<ProcessingStep>()));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), Id.New<ArtifactBundle>(), Id.New<ProcessingStep>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionAndProcessingJob_SamePipeline_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), Id.New<ProductionStep>()));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), Id.New<ArtifactBundle>(), Id.New<ProcessingStep>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionJob_SamePipelineDifferentStep_NoSuperseding()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), Id.New<ProductionStep>()));
        var job2 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), Id.New<ProductionStep>()));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ProductionJob_AfterFirstInProgress_SecondIsQueuedScheduled()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProductionStep>();

        var job1 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId));

        service.UpdateJob<ProductionJob>(job1.Id, j => j with { Status = new InProgress(DateTimeOffset.UtcNow) });

        var job2 = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId));

        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<InProgress>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ThreeScheduled_SameKey_OnlyNewestSurvives_SupersessionChainTerminates()
    {
        var (service, store) = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProcessingStep>();
        var bundleId = Id.New<ArtifactBundle>();

        var job1 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), bundleId, stepId));
        var job2 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), bundleId, stepId));
        var job3 = CreateAndGet(service, s => s.CreateProcessingJob(pipelineId, Id.New<JobGroup>(), bundleId, stepId));

        // Newest (job3) is the single survivor; the two older jobs are obsolete and following their
        // SupersedingJobId always converges on job3 with no cycle (strict total order).
        await Assert.That(GetJob(store, job3.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(GetJob(store, job1.Id).Status).IsTypeOf<Obsolete>();
        await Assert.That(GetJob(store, job2.Id).Status).IsTypeOf<Obsolete>();
        await Assert.That(ResolveSurvivor(store, job1.Id)).IsEqualTo(job3.Id);
        await Assert.That(ResolveSurvivor(store, job2.Id)).IsEqualTo(job3.Id);
    }

    // Walk the SupersedingJobId chain to the single non-obsolete job; throws if it loops, so a
    // mutual-supersession cycle fails the test rather than hanging.
    private static Id<Job> ResolveSurvivor(EntityStore<Job> store, Id<Job> start)
    {
        var seen = new HashSet<Id<Job>>();
        var current = start;
        while (GetJob(store, current).Status is Obsolete obsolete)
        {
            if (!seen.Add(current))
                throw new InvalidOperationException($"Supersession cycle detected at job '{current}'.");
            current = obsolete.SupersedingJobId;
        }
        return current;
    }

    // Requirement #3: under concurrent scheduling for one (pipeline, step) key, supersession is a
    // strict total order — exactly one runnable job survives and no two jobs name each other.
    // Reproduces the mutual-supersession deadlock pre-fix (each handler obsoleted "the other").
    [Test]
    public async Task ConcurrentCreations_SameKey_NoMutualSupersession()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var (service, store) = CreateServices();
            var pipelineId = Id.New<Pipeline>();
            var stepId = Id.New<ProductionStep>();

            using var barrier = new Barrier(2);
            var created = new Id<Job>[2];

            void Create(int index)
            {
                barrier.SignalAndWait();
                created[index] = CreateAndGet(service, s => s.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId)).Id;
            }

            var t1 = Task.Run(() => Create(0));
            var t2 = Task.Run(() => Create(1));
            await Task.WhenAll(t1, t2);

            var jobs = created.Select(id => GetJob(store, id)).ToArray();
            var scheduled = jobs.Where(j => j.Status is Scheduled).ToArray();
            var obsolete = jobs.Where(j => j.Status is Obsolete).ToArray();

            // Exactly one survives; the loser points at the survivor — never each other (a cycle).
            await Assert.That(scheduled).Count().IsEqualTo(1);
            await Assert.That(obsolete).Count().IsEqualTo(1);
            await Assert.That(((Obsolete)obsolete[0].Status).SupersedingJobId).IsEqualTo(scheduled[0].Id);
        }
    }
}
