using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class JobGroupCompletionServiceTests
{
    private record Services(
        EntityStore<Job> JobStore,
        JobService JobService,
        JobGroupService JobGroupService,
        JobGroupCompletionService CompletionService,
        JobEvents Events);

    private static Services CreateServices()
    {
        var jobStore = new EntityStore<Job>([]);
        var jobGroupStore = new EntityStore<JobGroup>([]);
        var bundleStore = new EntityStore<ArtifactBundle>([]);
        var idProvider = new IdProvider();
        var timeProvider = new MonotonicTimeProvider();
        var events = new JobEvents();

        jobStore.OnUpdated.Subscribe(events.OnUpdated.Invoke);

        var jobGroupService = new JobGroupService(jobGroupStore, idProvider, timeProvider);
        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, jobGroupService, idProvider, timeProvider);
        var bundleService = new ArtifactBundleService(bundleStore);

        var completionService = new JobGroupCompletionService(
            jobService, jobGroupService, bundleService, new JobGroupCompletionTracker(), events,
            NullLogger<JobGroupCompletionService>.Instance);

        events.OnUpdated.Subscribe(completionService.HandleJobUpdated);

        return new Services(jobStore, jobService, jobGroupService, completionService, events);
    }

    // Acceptance test for requirement #1: when the last jobs of a group reach a terminal state
    // concurrently, every watcher thread observes the whole group terminal and calls
    // HandleJobUpdated. Pre-fix, each invocation fired OnGroupCompleted (double-promote → deadlock).
    // Drive both observations directly with both jobs already Done; the group must complete once.
    [Test]
    public async Task ConcurrentTerminalObservations_FireGroupCompletedOnce()
    {
        var svc = CreateServices();
        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();
        var group = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);

        var now = DateTimeOffset.UtcNow;
        var job1 = new ProductionJob(Id.New<Job>(), pipelineId, now, new Done(now, now), group.Id, Id.New<ProductionStep>());
        var job2 = new ProductionJob(Id.New<Job>(), pipelineId, now, new Done(now, now), group.Id, Id.New<ProductionStep>());
        svc.JobStore.Set(job1);
        svc.JobStore.Set(job2);

        var completions = 0;
        svc.Events.OnGroupCompleted.Subscribe(_ => Interlocked.Increment(ref completions));

        // Both watcher threads see the group fully terminal — simulate by handling both transitions.
        svc.CompletionService.HandleJobUpdated(job1.Id);
        svc.CompletionService.HandleJobUpdated(job2.Id);

        await Assert.That(completions).IsEqualTo(1);
    }

    // Same guarantee under real thread contention: two parallel production jobs driven to Done on
    // two threads, repeated to surface the race. Exactly one OnGroupCompleted per run.
    [Test]
    public async Task TwoParallelProductionJobs_CompletedOnTwoThreads_FireGroupCompletedOnce()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var svc = CreateServices();
            var pipelineId = Id.New<Pipeline>();
            var bundleId = Id.New<ArtifactBundle>();
            var group = svc.JobGroupService.CreateProductionGroup(pipelineId, bundleId);

            var job1 = Create(svc, pipelineId, group.Id);
            var job2 = Create(svc, pipelineId, group.Id);

            var completions = 0;
            svc.Events.OnGroupCompleted.Subscribe(_ => Interlocked.Increment(ref completions));

            using var barrier = new Barrier(2);

            void Complete(Id<Job> id)
            {
                barrier.SignalAndWait();
                var now = DateTimeOffset.UtcNow;
                svc.JobService.UpdateJob<ProductionJob>(id, j => j with { Status = new Done(now, now) });
            }

            await Task.WhenAll(Task.Run(() => Complete(job1)), Task.Run(() => Complete(job2)));

            await Assert.That(completions).IsEqualTo(1);
        }
    }

    private static Id<Job> Create(Services svc, Id<Pipeline> pipelineId, Id<JobGroup> groupId)
    {
        var result = svc.JobService.CreateProductionJob(pipelineId, groupId, Id.New<ProductionStep>());
        result.TryPickProblems(out _, out var job);
        return job!.Id;
    }
}
