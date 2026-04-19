using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class JobRunnerTests
{
    private sealed class NullApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class TestHarness(
        JobRunner runner,
        JobService jobService,
        NoOpJobExecutorPendingStore pendingStore,
        JobWatcherRegistry registry,
        EntityStore<Job> store)
    {
        public JobRunner Runner => runner;
        public JobService JobService => jobService;
        public NoOpJobExecutorPendingStore PendingStore => pendingStore;
        public JobWatcherRegistry Registry => registry;
        public EntityStore<Job> Store => store;
    }

    private static TestHarness CreateRunner(int maxConcurrentJobs = 4)
    {
        var jobStore = new EntityStore<Job>([]);
        var pendingStore = new NoOpJobExecutorPendingStore();
        var registry = new JobWatcherRegistry(NullLogger<JobWatcherRegistry>.Instance);
        var lifetime = new NullApplicationLifetime();

        var services = new ServiceCollection();
        services.AddSingleton(jobStore);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(pendingStore);
        services.AddSingleton(registry);
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddTransient(_ => new JobService(
            NullLogger<JobService>.Instance, jobStore, new IdProvider(), TimeProvider.System));
        services.AddTransient<JobQueueService>(_ => new JobQueueService(jobStore));
        services.AddTransient<IJobExecutor>(sp => new NoOpJobExecutor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            registry,
            lifetime,
            pendingStore,
            sp.GetRequiredService<JobService>(),
            TimeProvider.System,
            NullLogger<NoOpJobExecutor>.Instance));
        var sp = services.BuildServiceProvider();

        var runner = new JobRunner(sp, registry, NullLogger<JobRunner>.Instance)
        {
            MaxConcurrentJobs = maxConcurrentJobs,
        };

        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, new IdProvider(), TimeProvider.System);

        return new TestHarness(runner, jobService, pendingStore, registry, jobStore);
    }

    private static Id<Job> CreateProductionJob(JobService jobService)
    {
        var result = jobService.CreateProductionJob(Id.New<Pipeline>(), Id.New<JobGroup>(), Id.New<ProductionStep>());
        result.TryPickProblems(out _, out var job);
        return job!.Id;
    }

    private static Id<Job> CreateProcessingJob(JobService jobService)
    {
        var result = jobService.CreateProcessingJob(Id.New<Pipeline>(), Id.New<JobGroup>(), Id.New<ArtifactBundle>(), Id.New<ProcessingStep>());
        result.TryPickProblems(out _, out var job);
        return job!.Id;
    }

    private static async Task WaitForPendingAsync(NoOpJobExecutorPendingStore pendingStore, Id<Job> jobId, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        while (!pendingStore.HasPendingJob(jobId))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Job '{jobId}' did not become pending within {timeout}.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ProductionJob_FinishSuccess_TransitionsToDone()
    {
        var h = CreateRunner();
        var jobId = CreateProductionJob(h.JobService);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());

        await Task.Delay(100);
        cts.Cancel();

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task ProcessingJob_FinishSuccess_TransitionsToDone()
    {
        var h = CreateRunner();
        var jobId = CreateProcessingJob(h.JobService);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());

        await Task.Delay(100);
        cts.Cancel();

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task Job_FinishFailure_TransitionsToFailed()
    {
        var h = CreateRunner();
        var jobId = CreateProductionJob(h.JobService);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Failure("script exited with code 1"));

        await Task.Delay(100);
        cts.Cancel();

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Failed>();
        var failed = (Failed)job.Status;
        await Assert.That(failed.Reason).IsEqualTo("script exited with code 1");
    }

    [Test]
    public async Task Job_BeforeFinish_IsInProgress()
    {
        var h = CreateRunner();
        var jobId = CreateProductionJob(h.JobService);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<InProgress>();

        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());
        await Task.Delay(100);
        cts.Cancel();
    }

    [Test]
    public async Task MaxConcurrentJobs_LimitsConcurrency()
    {
        var h = CreateRunner(maxConcurrentJobs: 2);

        var job1 = CreateProductionJob(h.JobService);
        var job2 = CreateProductionJob(h.JobService);
        var job3 = CreateProductionJob(h.JobService);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, job1);
        await WaitForPendingAsync(h.PendingStore, job2);

        // Third job should still be scheduled because max is 2
        await Task.Delay(200);
        await Assert.That(h.PendingStore.HasPendingJob(job3)).IsFalse();
        await Assert.That(h.Registry.ActiveCount).IsEqualTo(2);
        h.Store.TryGet(job3, out var job3Entity);
        await Assert.That(job3Entity!.Status).IsTypeOf<Scheduled>();

        // Finish one job to free a slot
        h.PendingStore.Finish(job1, new NoOpJobResult.Success());

        // Now job3 should get picked up
        await WaitForPendingAsync(h.PendingStore, job3);
        await Assert.That(h.PendingStore.HasPendingJob(job3)).IsTrue();

        h.PendingStore.Finish(job2, new NoOpJobResult.Success());
        h.PendingStore.Finish(job3, new NoOpJobResult.Success());
        await Task.Delay(100);
        cts.Cancel();
    }

    [Test]
    public async Task MultipleJobs_AllComplete()
    {
        var h = CreateRunner();

        var job1 = CreateProductionJob(h.JobService);
        var job2 = CreateProcessingJob(h.JobService);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, job1);
        await WaitForPendingAsync(h.PendingStore, job2);

        h.PendingStore.Finish(job1, new NoOpJobResult.Success());
        h.PendingStore.Finish(job2, new NoOpJobResult.Success());

        await Task.Delay(100);
        cts.Cancel();

        h.Store.TryGet(job1, out var j1);
        h.Store.TryGet(job2, out var j2);
        await Assert.That(j1!.Status).IsTypeOf<Done>();
        await Assert.That(j2!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task ScheduledJob_WaitsWhileSameKeyIsInProgress_RunsAfterCompletion()
    {
        var h = CreateRunner();
        var pipelineId = Id.New<Pipeline>();
        var stepId = Id.New<ProductionStep>();

        var first = h.JobService.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId);
        first.TryPickProblems(out _, out var firstJob);
        var firstId = firstJob!.Id;

        var second = h.JobService.CreateProductionJob(pipelineId, Id.New<JobGroup>(), stepId);
        second.TryPickProblems(out _, out var secondJob);
        var secondId = secondJob!.Id;

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, firstId);

        // Second should be held — same (pipeline, step) key, first is InProgress.
        await Task.Delay(200);
        await Assert.That(h.PendingStore.HasPendingJob(secondId)).IsFalse();
        h.Store.TryGet(secondId, out var secondEntity);
        await Assert.That(secondEntity!.Status).IsTypeOf<Scheduled>();

        // Finish the first; second should now be dispatched.
        h.PendingStore.Finish(firstId, new NoOpJobResult.Success());
        await WaitForPendingAsync(h.PendingStore, secondId);

        h.PendingStore.Finish(secondId, new NoOpJobResult.Success());
        await Task.Delay(100);
        cts.Cancel();

        h.Store.TryGet(firstId, out var j1);
        h.Store.TryGet(secondId, out var j2);
        await Assert.That(j1!.Status).IsTypeOf<Done>();
        await Assert.That(j2!.Status).IsTypeOf<Done>();
    }
}
