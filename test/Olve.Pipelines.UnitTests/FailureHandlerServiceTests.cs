using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.FailureHandlers;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.UnitTests;

public class FailureHandlerServiceTests
{
    private const string Namespace = "olve-runners";

    private sealed record BareJob(string Name, string Image, string Script, IReadOnlyDictionary<string, string>? Env);

    private sealed class CapturingKubernetesClient : IKubernetesClient
    {
        public ConcurrentQueue<BareJob> Jobs { get; } = new();

        public Task CreateBareJobAsync(string ns, string name, string image, string script, IReadOnlyDictionary<string, string>? env, string? runtimeClassName = null, CancellationToken ct = default)
        {
            Jobs.Enqueue(new BareJob(name, image, script, env));
            return Task.CompletedTask;
        }

        public Task CreateJobAsync(string ns, KubernetesJobSpec spec, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KubernetesJobStatus> GetJobStatusAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KubernetesJobStatus?> TryGetJobStatusAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetPodLogsAsync(string ns, string jobName, string? container = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteJobAsync(string ns, string jobName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, string>?> GetSecretAsync(string ns, string secretName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSecretAsync(string ns, string secretName, Dictionary<string, string> data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string ns, string secretName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record Harness(
        PipelineService Pipelines,
        ProductionStepService ProductionSteps,
        JobGroupService JobGroups,
        JobService Jobs,
        FailureHandlerBindingService Bindings,
        CapturingKubernetesClient K8s,
        FailureHandlerService Service);

    private static Harness CreateHarness()
    {
        var idProvider = new IdProvider();
        var timeProvider = new TimeProviderMake().Instance();

        var pipelineStore = new EntityStore<Pipeline>([]);
        var pipelineService = new PipelineService(pipelineStore, idProvider);

        var productionStepStore = new EntityStore<ProductionStep>([]);
        var productionStepConfig = new AttachmentStore<ProductionStep, StepConfiguration>(productionStepStore);
        var productionStepService = new ProductionStepService(productionStepStore, productionStepConfig, idProvider);

        var processingStepStore = new EntityStore<ProcessingStep>([]);
        var processingStepConfig = new AttachmentStore<ProcessingStep, StepConfiguration>(processingStepStore);
        var processingStepService = new ProcessingStepService(processingStepStore, processingStepConfig, idProvider);

        var jobStore = new EntityStore<Job>([]);
        var jobGroupStore = new EntityStore<JobGroup>([]);
        var jobGroupService = new JobGroupService(jobGroupStore, idProvider, timeProvider);
        var jobService = new JobService(NullLogger<JobService>.Instance, jobStore, jobGroupService, idProvider, timeProvider);

        var bindingStore = new AttachmentStore<Pipeline, FailureHandlerBindings>(pipelineStore);
        var bindingService = new FailureHandlerBindingService(bindingStore);
        var library = new FailureHandlerLibrary();
        var k8s = new CapturingKubernetesClient();
        var options = new KubernetesOptions(Namespace, "", "", "", "https://s3", false);

        var service = new FailureHandlerService(
            jobGroupService, jobService, pipelineService, productionStepService, processingStepService,
            bindingService, library, k8s, options, NullLogger<FailureHandlerService>.Instance);

        return new Harness(pipelineService, productionStepService, jobGroupService, jobService, bindingService, k8s, service);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    /// <summary>Submission is fire-and-forget; wait briefly for the expected number of K8s jobs.</summary>
    private static async Task<IReadOnlyList<BareJob>> WaitForJobsAsync(CapturingKubernetesClient k8s, int expected)
    {
        for (var i = 0; i < 200 && k8s.Jobs.Count < expected; i++)
            await Task.Delay(10);

        // A short grace so an unexpected extra submission can also land before we assert the count.
        await Task.Delay(50);
        return k8s.Jobs.ToArray();
    }

    /// <summary>Creates a production group with one failed production step and returns (group, stepName).</summary>
    private static (Id<JobGroup> GroupId, string StepName) FailedProductionGroup(Harness h, Id<Pipeline> pipelineId, string stepName, string reason)
    {
        var step = Pick(h.ProductionSteps.Create(pipelineId, stepName));
        var group = h.JobGroups.CreateProductionGroup(pipelineId, Id.New<ArtifactBundle>());
        var job = Pick(h.Jobs.CreateProductionJob(pipelineId, group.Id, step.Id));
        var now = DateTimeOffset.UtcNow;
        h.Jobs.UpdateJob<Job>(job.Id, j => j with { Status = new Failed(now, now, reason) });
        return (group.Id, step.Name);
    }

    [Test]
    public async Task WholePipelineBinding_RunsOneHandler_WithContextAndConfigEnv()
    {
        var h = CreateHarness();
        var pipeline = Pick(h.Pipelines.Create("my-pipeline"));
        var (groupId, stepName) = FailedProductionGroup(h, pipeline.Id, "build", "exit code 1");

        h.Bindings.Set(pipeline.Id, [
            new FailureHandlerBinding(
                FailureHandlerLibrary.AoeTriage,
                Steps: [],
                Env: new Dictionary<string, string> { ["AOE_BASE_URL"] = "http://aoe.aoe.svc.cluster.local" }),
        ]);

        h.Service.HandleGroupFailed(groupId);
        var jobs = await WaitForJobsAsync(h.K8s, 1);

        await Assert.That(jobs).Count().IsEqualTo(1);
        await Assert.That(jobs[0].Image).IsEqualTo("alpine:3.20");
        await Assert.That(jobs[0].Env![FailureContext.PipelineNameKey]).IsEqualTo("my-pipeline");
        await Assert.That(jobs[0].Env![FailureContext.FailedStepKey]).IsEqualTo(stepName);
        await Assert.That(jobs[0].Env![FailureContext.FailedStepKindKey]).IsEqualTo("production");
        await Assert.That(jobs[0].Env![FailureContext.FailureReasonKey]).IsEqualTo("exit code 1");
        await Assert.That(jobs[0].Env!["AOE_BASE_URL"]).IsEqualTo("http://aoe.aoe.svc.cluster.local");
    }

    [Test]
    public async Task PerStepBinding_MatchingFailedStep_Runs()
    {
        var h = CreateHarness();
        var pipeline = Pick(h.Pipelines.Create("p"));
        var (groupId, _) = FailedProductionGroup(h, pipeline.Id, "build", "boom");

        h.Bindings.Set(pipeline.Id, [
            new FailureHandlerBinding(FailureHandlerLibrary.AoeTriage, Steps: ["build"], Env: new Dictionary<string, string>()),
        ]);

        h.Service.HandleGroupFailed(groupId);
        var jobs = await WaitForJobsAsync(h.K8s, 1);

        await Assert.That(jobs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task PerStepBinding_NonMatchingStep_DoesNotRun()
    {
        var h = CreateHarness();
        var pipeline = Pick(h.Pipelines.Create("p"));
        var (groupId, _) = FailedProductionGroup(h, pipeline.Id, "build", "boom");

        h.Bindings.Set(pipeline.Id, [
            new FailureHandlerBinding(FailureHandlerLibrary.AoeTriage, Steps: ["some-other-step"], Env: new Dictionary<string, string>()),
        ]);

        h.Service.HandleGroupFailed(groupId);
        var jobs = await WaitForJobsAsync(h.K8s, 0);

        await Assert.That(jobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task UnknownHandlerName_DoesNotRun_AndDoesNotThrow()
    {
        var h = CreateHarness();
        var pipeline = Pick(h.Pipelines.Create("p"));
        var (groupId, _) = FailedProductionGroup(h, pipeline.Id, "build", "boom");

        h.Bindings.Set(pipeline.Id, [
            new FailureHandlerBinding("does-not-exist", Steps: [], Env: new Dictionary<string, string>()),
        ]);

        h.Service.HandleGroupFailed(groupId);
        var jobs = await WaitForJobsAsync(h.K8s, 0);

        await Assert.That(jobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task NoBindings_DoesNotRun()
    {
        var h = CreateHarness();
        var pipeline = Pick(h.Pipelines.Create("p"));
        var (groupId, _) = FailedProductionGroup(h, pipeline.Id, "build", "boom");

        h.Service.HandleGroupFailed(groupId);
        var jobs = await WaitForJobsAsync(h.K8s, 0);

        await Assert.That(jobs).Count().IsEqualTo(0);
    }

    [Test]
    public async Task WholePipelineBinding_MultipleFailedSteps_FiresOnce()
    {
        var h = CreateHarness();
        var pipeline = Pick(h.Pipelines.Create("p"));

        // Two production steps in the SAME group, both failed.
        var stepA = Pick(h.ProductionSteps.Create(pipeline.Id, "build-a"));
        var stepB = Pick(h.ProductionSteps.Create(pipeline.Id, "build-b"));
        var group = h.JobGroups.CreateProductionGroup(pipeline.Id, Id.New<ArtifactBundle>());
        var jobA = Pick(h.Jobs.CreateProductionJob(pipeline.Id, group.Id, stepA.Id));
        var jobB = Pick(h.Jobs.CreateProductionJob(pipeline.Id, group.Id, stepB.Id));
        var now = DateTimeOffset.UtcNow;
        h.Jobs.UpdateJob<Job>(jobA.Id, j => j with { Status = new Failed(now, now, "a") });
        h.Jobs.UpdateJob<Job>(jobB.Id, j => j with { Status = new Failed(now, now, "b") });

        h.Bindings.Set(pipeline.Id, [
            new FailureHandlerBinding(FailureHandlerLibrary.AoeTriage, Steps: [], Env: new Dictionary<string, string>()),
        ]);

        h.Service.HandleGroupFailed(group.Id);
        var jobs = await WaitForJobsAsync(h.K8s, 1);

        await Assert.That(jobs).Count().IsEqualTo(1);
    }
}
