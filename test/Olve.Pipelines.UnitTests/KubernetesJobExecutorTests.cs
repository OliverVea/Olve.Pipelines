using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class KubernetesJobExecutorTests
{
    private sealed class FakeKubernetesClient : IKubernetesClient
    {
        public KubernetesJobStatus? InitialStatus { get; set; }
        public List<KubernetesJobStatus> PollSequence { get; } = new();
        public int CreateJobCallCount { get; private set; }
        public int CreateSecretCallCount { get; private set; }
        public int DeleteSecretCallCount { get; private set; }

        public Task CreateJobAsync(string ns, KubernetesJobSpec spec, CancellationToken ct = default)
        {
            CreateJobCallCount++;
            return Task.CompletedTask;
        }

        public Task CreateBareJobAsync(string ns, string name, string image, string script, IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default)
            => Task.FromResult(InitialStatus);

        public Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default)
        {
            if (PollSequence.Count == 0)
                return Task.FromResult(new KubernetesJobStatus(jobName, JobPhase.Succeeded));

            var next = PollSequence[0];
            if (PollSequence.Count > 1) PollSequence.RemoveAt(0);
            return Task.FromResult(next);
        }

        public Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default) => Task.CompletedTask;

        public Task CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
        {
            CreateSecretCallCount++;
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default)
            => Task.FromResult<Dictionary<string, string>?>(null);

        public Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default)
        {
            DeleteSecretCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoSecretsStore : IPipelineSecretStore
    {
        public Task<bool> HasSecretsAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class StubS3CredentialsProvider : ICredentialsProvider<S3Credentials>
    {
        public Task<S3Credentials> GetCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult(new S3Credentials("access", "secret"));
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed record Harness(
        KubernetesJobExecutor Executor,
        FakeKubernetesClient K8s,
        EntityStore<Job> JobStore,
        JobWatcherRegistry Registry,
        JobService JobService,
        Id<Pipeline> PipelineId,
        Id<JobGroup> JobGroupId,
        Id<ArtifactBundle> BundleId,
        Id<ProductionStep> StepId,
        FakeApplicationLifetime Lifetime);

    private static Harness BuildHarness(IKubernetesClient? k8sOverride = null)
    {
        var k8s = k8sOverride as FakeKubernetesClient ?? new FakeKubernetesClient();

        var pipelineId = Id.New<Pipeline>();
        var bundleId = Id.New<ArtifactBundle>();
        var stepId = Id.New<ProductionStep>();

        var pipelineStore = new EntityStore<Pipeline>([new Pipeline(pipelineId, "p")]);

        var prodStore = new EntityStore<ProductionStep>([new ProductionStep(stepId, "step", pipelineId)]);
        var prodConfig = new AttachmentStore<ProductionStep, StepConfiguration>(prodStore);
        prodConfig.Set(stepId, new StepConfiguration("alpine", "echo hi"));
        var prodSvc = new ProductionStepService(prodStore, prodConfig, new IdProvider());

        var procStore = new EntityStore<ProcessingStep>([]);
        var procConfig = new AttachmentStore<ProcessingStep, StepConfiguration>(procStore);
        var procSvc = new ProcessingStepService(procStore, procConfig, new IdProvider());

        var jobStore = new EntityStore<Job>([]);
        var jobGroupStore = new EntityStore<JobGroup>([]);
        var idProvider = new IdProvider();
        var time = TimeProvider.System;
        var jobGroupSvc = new JobGroupService(jobGroupStore, idProvider, time);
        var jobSvc = new JobService(NullLogger<JobService>.Instance, jobStore, jobGroupSvc, idProvider, time);

        var group = jobGroupSvc.CreateProductionGroup(pipelineId, bundleId);

        var registry = new JobWatcherRegistry(NullLogger<JobWatcherRegistry>.Instance);
        var lifetime = new FakeApplicationLifetime();
        var options = new KubernetesOptions("default", "alpine", "minio/mc", "bucket", "https://s3", false);
        var storage = new StorageOptions("bucket");

        var services = new ServiceCollection();
        services.AddSingleton(jobStore);
        services.AddSingleton(jobGroupStore);
        services.AddSingleton(registry);
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton(k8s);
        services.AddSingleton(options);
        services.AddSingleton(storage);
        services.AddSingleton(time);
        services.AddSingleton<ProductionStepService>(prodSvc);
        services.AddSingleton<ProcessingStepService>(procSvc);
        services.AddSingleton(jobGroupSvc);
        services.AddSingleton(jobSvc);
        services.AddSingleton<IPipelineSecretStore>(new NoSecretsStore());
        services.AddSingleton<ICredentialsProvider<S3Credentials>>(new StubS3CredentialsProvider());
        services.AddTransient<IJobExecutor>(sp => new KubernetesJobExecutor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            registry,
            lifetime,
            k8s,
            options,
            prodSvc,
            procSvc,
            jobGroupSvc,
            sp.GetRequiredService<IPipelineSecretStore>(),
            sp.GetRequiredService<ICredentialsProvider<S3Credentials>>(),
            s3: null!,
            storage,
            jobSvc,
            time,
            NullLogger<KubernetesJobExecutor>.Instance));

        var sp = services.BuildServiceProvider();
        var executor = (KubernetesJobExecutor)sp.GetRequiredService<IJobExecutor>();

        return new Harness(executor, k8s, jobStore, registry, jobSvc, pipelineId, group.Id, bundleId, stepId, lifetime);
    }

    private static Id<Job> CreateScheduledProductionJob(Harness h)
    {
        var r = h.JobService.CreateProductionJob(h.PipelineId, h.JobGroupId, h.StepId);
        r.TryPickProblems(out _, out var job);
        return job!.Id;
    }

    private static async Task WaitForWatcherComplete(Harness h, Id<Job> id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (h.Registry.IsRunning(id))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Watcher for '{id}' did not complete in time.");
            await Task.Delay(20);
        }
    }

    [Test]
    public async Task FreshSubmit_NoExistingK8sJob_SubmitsThenCompletes()
    {
        var h = BuildHarness();
        h.K8s.InitialStatus = null;
        h.K8s.PollSequence.Add(new KubernetesJobStatus("x", JobPhase.Succeeded));

        var jobId = CreateScheduledProductionJob(h);

        h.Executor.EnsureRunning(jobId);
        await WaitForWatcherComplete(h, jobId);

        await Assert.That(h.K8s.CreateJobCallCount).IsEqualTo(1);
        h.JobStore.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task Reattach_ExistingRunningK8sJob_DoesNotCreateJob()
    {
        var h = BuildHarness();
        h.K8s.InitialStatus = new KubernetesJobStatus("x", JobPhase.Running);
        h.K8s.PollSequence.Add(new KubernetesJobStatus("x", JobPhase.Succeeded));

        var jobId = CreateScheduledProductionJob(h);

        h.Executor.EnsureRunning(jobId);
        await WaitForWatcherComplete(h, jobId);

        await Assert.That(h.K8s.CreateJobCallCount).IsEqualTo(0);
        h.JobStore.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task Reattach_ExistingSucceededK8sJob_SkipsPollingAndCompletes()
    {
        var h = BuildHarness();
        h.K8s.InitialStatus = new KubernetesJobStatus("x", JobPhase.Succeeded);
        // PollSequence empty — a bug that polled would throw since we'd use default

        var jobId = CreateScheduledProductionJob(h);

        h.Executor.EnsureRunning(jobId);
        await WaitForWatcherComplete(h, jobId);

        await Assert.That(h.K8s.CreateJobCallCount).IsEqualTo(0);
        h.JobStore.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<Done>();
    }

    [Test]
    public async Task TerminalCompletion_DeletesPerJobS3Secret()
    {
        var h = BuildHarness();
        h.K8s.InitialStatus = null;
        h.K8s.PollSequence.Add(new KubernetesJobStatus("x", JobPhase.Succeeded));

        var jobId = CreateScheduledProductionJob(h);

        h.Executor.EnsureRunning(jobId);
        await WaitForWatcherComplete(h, jobId);

        await Assert.That(h.K8s.CreateSecretCallCount).IsEqualTo(1);
        await Assert.That(h.K8s.DeleteSecretCallCount).IsEqualTo(1);
    }

    // Regression: a self-deploy step restarts this controller mid-run, cancelling the watcher while the
    // K8s Job is still running. The per-job S3 secret must NOT be deleted then — deleting it breaks the
    // Job's pending s3-upload container and wedges the job. The job stays InProgress so the next startup
    // reattaches with the secret intact.
    [Test]
    public async Task CancellationDuringShutdown_PreservesPerJobS3Secret()
    {
        var h = BuildHarness();
        h.K8s.InitialStatus = new KubernetesJobStatus("x", JobPhase.Running);

        var jobId = CreateScheduledProductionJob(h);

        h.Lifetime.StopApplication();
        h.Executor.EnsureRunning(jobId);
        await WaitForWatcherComplete(h, jobId);

        await Assert.That(h.K8s.CreateSecretCallCount).IsEqualTo(1);
        await Assert.That(h.K8s.DeleteSecretCallCount).IsEqualTo(0);
        h.JobStore.TryGet(jobId, out var job);
        await Assert.That(job!.Status).IsTypeOf<InProgress>();
    }

    [Test]
    public async Task Reattach_InProgressJobRecord_KeepsOriginalStartTime()
    {
        var h = BuildHarness();
        h.K8s.InitialStatus = new KubernetesJobStatus("x", JobPhase.Running);
        h.K8s.PollSequence.Add(new KubernetesJobStatus("x", JobPhase.Succeeded));

        var jobId = CreateScheduledProductionJob(h);

        var originalStart = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        h.JobService.UpdateJob<Job>(jobId, j => j with { Status = new InProgress(originalStart) });

        h.Executor.EnsureRunning(jobId);
        await WaitForWatcherComplete(h, jobId);

        h.JobStore.TryGet(jobId, out var job);
        var done = (Done)job!.Status;
        await Assert.That(done.StartedAt).IsEqualTo(originalStart);
    }
}
