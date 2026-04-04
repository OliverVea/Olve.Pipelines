using Microsoft.Extensions.DependencyInjection;
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
    private static (JobRunner Runner, JobService JobService, NoOpJobExecutor Executor, EntityStore<Job> Store) CreateRunner(
        int maxConcurrentJobs = 4)
    {
        var jobStore = new EntityStore<Job>([]);
        var executor = new NoOpJobExecutor(NullLogger<NoOpJobExecutor>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(jobStore);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddTransient(_ => new JobService(
            NullLogger<JobService>.Instance, jobStore, new IdProvider(), TimeProvider.System));
        services.AddTransient<JobQueueService>(_ => new JobQueueService(jobStore));
        services.AddSingleton<IJobExecutor>(executor);
        var sp = services.BuildServiceProvider();

        var runner = new JobRunner(sp, NullLogger<JobRunner>.Instance)
        {
            MaxConcurrentJobs = maxConcurrentJobs,
        };

        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, new IdProvider(), TimeProvider.System);

        return (runner, jobService, executor, jobStore);
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

    private static async Task WaitForPendingAsync(NoOpJobExecutor executor, Id<Job> jobId, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        while (!executor.HasPendingJob(jobId))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Job '{jobId}' did not become pending within {timeout}.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ProductionJob_FinishSuccess_TransitionsToDone()
    {
        var (runner, jobService, executor, store) = CreateRunner();
        var jobId = CreateProductionJob(jobService);

        using var cts = new CancellationTokenSource();
        _ = runner.StartAsync(cts.Token);

        await WaitForPendingAsync(executor, jobId);
        executor.Finish(jobId, new JobExecutionResult.Success());

        await Task.Delay(100);
        cts.Cancel();

        store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task ProcessingJob_FinishSuccess_TransitionsToDone()
    {
        var (runner, jobService, executor, store) = CreateRunner();
        var jobId = CreateProcessingJob(jobService);

        using var cts = new CancellationTokenSource();
        _ = runner.StartAsync(cts.Token);

        await WaitForPendingAsync(executor, jobId);
        executor.Finish(jobId, new JobExecutionResult.Success());

        await Task.Delay(100);
        cts.Cancel();

        store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task Job_FinishFailure_TransitionsToFailed()
    {
        var (runner, jobService, executor, store) = CreateRunner();
        var jobId = CreateProductionJob(jobService);

        using var cts = new CancellationTokenSource();
        _ = runner.StartAsync(cts.Token);

        await WaitForPendingAsync(executor, jobId);
        executor.Finish(jobId, new JobExecutionResult.Failure("script exited with code 1"));

        await Task.Delay(100);
        cts.Cancel();

        store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Failed>();
        var failed = (Failed)job.Status;
        await Assert.That(failed.Reason).IsEqualTo("script exited with code 1");
    }

    [Test]
    public async Task Job_BeforeFinish_IsInProgress()
    {
        var (runner, jobService, executor, store) = CreateRunner();
        var jobId = CreateProductionJob(jobService);

        using var cts = new CancellationTokenSource();
        _ = runner.StartAsync(cts.Token);

        await WaitForPendingAsync(executor, jobId);

        store.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<InProgress>();

        executor.Finish(jobId, new JobExecutionResult.Success());
        await Task.Delay(100);
        cts.Cancel();
    }

    [Test]
    public async Task MaxConcurrentJobs_LimitsConcurrency()
    {
        var (runner, jobService, executor, store) = CreateRunner(maxConcurrentJobs: 2);

        var job1 = CreateProductionJob(jobService);
        var job2 = CreateProductionJob(jobService);
        var job3 = CreateProductionJob(jobService);

        using var cts = new CancellationTokenSource();
        _ = runner.StartAsync(cts.Token);

        await WaitForPendingAsync(executor, job1);
        await WaitForPendingAsync(executor, job2);

        // Third job should still be scheduled because max is 2
        await Task.Delay(200);
        await Assert.That(executor.HasPendingJob(job3)).IsFalse();
        store.TryGet(job3, out var job3Entity);
        await Assert.That(job3Entity!.Status).IsTypeOf<Scheduled>();

        // Finish one job to free a slot
        executor.Finish(job1, new JobExecutionResult.Success());

        // Now job3 should get picked up
        await WaitForPendingAsync(executor, job3);
        await Assert.That(executor.HasPendingJob(job3)).IsTrue();

        executor.Finish(job2, new JobExecutionResult.Success());
        executor.Finish(job3, new JobExecutionResult.Success());
        await Task.Delay(100);
        cts.Cancel();
    }

    [Test]
    public async Task MultipleJobs_AllComplete()
    {
        var (runner, jobService, executor, store) = CreateRunner();

        var job1 = CreateProductionJob(jobService);
        var job2 = CreateProcessingJob(jobService);

        using var cts = new CancellationTokenSource();
        _ = runner.StartAsync(cts.Token);

        await WaitForPendingAsync(executor, job1);
        await WaitForPendingAsync(executor, job2);

        executor.Finish(job1, new JobExecutionResult.Success());
        executor.Finish(job2, new JobExecutionResult.Success());

        await Task.Delay(100);
        cts.Cancel();

        store.TryGet(job1, out var j1);
        store.TryGet(job2, out var j2);
        await Assert.That(j1!.Status).IsTypeOf<Done>();
        await Assert.That(j2!.Status).IsTypeOf<Done>();
    }
}
