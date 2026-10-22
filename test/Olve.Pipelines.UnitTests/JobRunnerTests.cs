using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

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

    private sealed record TestHarness(
        JobRunner Runner,
        TestRunDomain Domain,
        NoOpJobExecutorPendingStore PendingStore,
        JobWatcherRegistry Registry)
    {
        public EntityStore<Attempt> Store => Domain.Attempts;
    }

    private static TestHarness CreateRunner(int maxConcurrentJobs = 4)
    {
        var domain = new TestRunDomain(TimeProvider.System);
        var pendingStore = new NoOpJobExecutorPendingStore();
        var registry = new JobWatcherRegistry(NullLogger<JobWatcherRegistry>.Instance);
        var lifetime = new NullApplicationLifetime();

        var services = new ServiceCollection();
        services.AddSingleton(domain.Attempts);
        services.AddSingleton(domain.AttemptService);
        services.AddSingleton(pendingStore);
        services.AddSingleton(registry);
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddTransient<JobQueueService>(_ => new JobQueueService(domain.Attempts));
        services.AddTransient<IJobExecutor>(sp => new NoOpJobExecutor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            registry,
            lifetime,
            pendingStore,
            domain.AttemptService,
            NullLogger<NoOpJobExecutor>.Instance));
        var sp = services.BuildServiceProvider();

        var runner = new JobRunner(sp, registry, NullLogger<JobRunner>.Instance)
        {
            MaxConcurrentJobs = maxConcurrentJobs,
        };

        return new TestHarness(runner, domain, pendingStore, registry);
    }

    // Each call schedules in a fresh pipeline, so attempts never share a (pipeline, step) key.
    private static Id<Attempt> CreateJob(TestHarness h, string stepName = "build") => h.Domain.Schedule(stepName).Id;

    private static async Task WaitForPendingAsync(NoOpJobExecutorPendingStore pendingStore, Id<Attempt> jobId, TimeSpan? timeout = null)
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

    // The runner records results asynchronously; a fixed sleep raced it on slow CI nodes.
    private static async Task WaitForFinishedAsync(TestHarness h, params Id<Attempt>[] jobIds)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (jobIds.Any(id => h.Store.TryGet(id, out var a) && a!.Status is Scheduled or InProgress))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Jobs did not finish within 5 s.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Job_FinishSuccess_TransitionsToSucceeded()
    {
        var h = CreateRunner();
        var jobId = CreateJob(h);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());

        await WaitForFinishedAsync(h, jobId);
        cts.Cancel();

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Succeeded>();
    }

    [Test]
    public async Task SecondStepJob_FinishSuccess_TransitionsToSucceeded()
    {
        var h = CreateRunner();
        var jobId = CreateJob(h, "deploy");

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());

        await WaitForFinishedAsync(h, jobId);
        cts.Cancel();

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Succeeded>();
    }

    [Test]
    public async Task Job_FinishFailure_TransitionsToFailed()
    {
        var h = CreateRunner();
        var jobId = CreateJob(h);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Failure("script exited with code 1"));

        await WaitForFinishedAsync(h, jobId);
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
        var jobId = CreateJob(h);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<InProgress>();

        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());
        await WaitForFinishedAsync(h, jobId);
        cts.Cancel();
    }

    [Test]
    public async Task MaxConcurrentJobs_LimitsConcurrency()
    {
        var h = CreateRunner(maxConcurrentJobs: 2);

        var job1 = CreateJob(h);
        var job2 = CreateJob(h);
        var job3 = CreateJob(h);

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

        var job1 = CreateJob(h);
        var job2 = CreateJob(h, "deploy");

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, job1);
        await WaitForPendingAsync(h.PendingStore, job2);

        h.PendingStore.Finish(job1, new NoOpJobResult.Success());
        h.PendingStore.Finish(job2, new NoOpJobResult.Success());

        await WaitForFinishedAsync(h, job1, job2);
        cts.Cancel();

        h.Store.TryGet(job1, out var j1);
        h.Store.TryGet(job2, out var j2);
        await Assert.That(j1!.Status).IsTypeOf<Succeeded>();
        await Assert.That(j2!.Status).IsTypeOf<Succeeded>();
    }

    // Regression: after a restart the watcher registry is empty, so an InProgress job has no live
    // watcher. It still owns its (pipeline, step) key, so the busy-key gate must not block it — the
    // dispatcher must reattach it. Before the fix it blocked itself forever and the pipeline wedged.
    [Test]
    public async Task InProgressJob_WithNoLiveWatcher_IsReattached()
    {
        var h = CreateRunner();
        var jobId = CreateJob(h);

        // Simulate the post-restart state: the job was persisted InProgress, but no watcher is running.
        await Assert.That(h.Domain.AttemptService.MarkInProgress(jobId)).Succeeded();

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        // Reattaches (becomes pending) rather than blocking itself behind its own key.
        await WaitForPendingAsync(h.PendingStore, jobId);

        h.PendingStore.Finish(jobId, new NoOpJobResult.Success());
        await WaitForFinishedAsync(h, jobId);
        cts.Cancel();

        h.Store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Succeeded>();
    }

    [Test]
    public async Task ScheduledJob_WaitsWhileSameKeyIsInProgress_RunsAfterCompletion()
    {
        var h = CreateRunner();
        // Two runs of one pipeline: same (pipeline, step) key. No obsoletion is wired here, so both
        // stay Scheduled and the busy-key gate alone must serialise them.
        var version = h.Domain.AddVersion(h.Domain.CreatePipeline().Id);
        var firstId = h.Domain.Schedule("build", h.Domain.CreateRun(version)).Id;
        var secondId = h.Domain.Schedule("build", h.Domain.CreateRun(version)).Id;

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
        await WaitForFinishedAsync(h, firstId, secondId);
        cts.Cancel();

        h.Store.TryGet(firstId, out var j1);
        h.Store.TryGet(secondId, out var j2);
        await Assert.That(j1!.Status).IsTypeOf<Succeeded>();
        await Assert.That(j2!.Status).IsTypeOf<Succeeded>();
    }

    [Test]
    public async Task Job_FinishWithOutput_RecordsOutputUnderAttemptKey()
    {
        var h = CreateRunner();
        var jobId = CreateJob(h);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Success("""{"image":"registry/app:1"}"""));

        await WaitForFinishedAsync(h, jobId);
        cts.Cancel();

        var attempt = h.Domain.Get(jobId);
        await Assert.That(attempt.Status).IsTypeOf<Succeeded>();
        await Assert.That(attempt.Output!.Json).IsEqualTo("""{"image":"registry/app:1"}""");
        await Assert.That(attempt.Output.Key).IsEqualTo(StorageLayout.AttemptOutputKey(attempt));
    }

    [Test]
    public async Task Job_FinishSkipped_TransitionsToSkippedWithReason()
    {
        var h = CreateRunner();
        var jobId = CreateJob(h);

        using var cts = new CancellationTokenSource();
        _ = h.Runner.StartAsync(cts.Token);

        await WaitForPendingAsync(h.PendingStore, jobId);
        h.PendingStore.Finish(jobId, new NoOpJobResult.Skip("nothing changed"));

        await WaitForFinishedAsync(h, jobId);
        cts.Cancel();

        var status = h.Domain.Get(jobId).Status;
        await Assert.That(status).IsTypeOf<Skipped>();
        await Assert.That(((Skipped)status).Reason).IsEqualTo("nothing changed");
        await Assert.That(((Skipped)status).StartedAt).IsNotNull();
    }
}
