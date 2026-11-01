using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PipelineJobCleanupServiceTests
{
    private static readonly Pipeline Deleted = new(new IdProvider().Create<Pipeline>(), "deleted");
    private static readonly string OtherHex = Guid.NewGuid().ToString("N");

    private sealed class FakeInventory(params ClusterJob[] jobs) : IKubernetesInventory
    {
        public Task<IReadOnlyList<ClusterJob>> ListJobsAsync(string ns, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClusterJob>>(jobs);

        public Task<IReadOnlyList<ClusterSecret>> ListSecretsAsync(string ns, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RefusingInventory : IKubernetesInventory
    {
        public Task<IReadOnlyList<ClusterJob>> ListJobsAsync(string ns, CancellationToken ct = default) => throw new HttpRequestException("refused");
        public Task<IReadOnlyList<ClusterSecret>> ListSecretsAsync(string ns, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ClusterJob Job(string name, string role, string? pipelineHex, bool finished) => new(
        name,
        pipelineHex is null
            ? new Dictionary<string, string> { ["olve-role"] = role }
            : new Dictionary<string, string> { ["olve-role"] = role, ["olve-pipeline"] = pipelineHex },
        DateTimeOffset.UnixEpoch, finished, 604800);

    private static PipelineJobCleanupService Service(IKubernetesInventory? inventory, FakeKubernetes k8s)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new KubernetesOptions("runners"));
        services.AddSingleton<IKubernetesClient>(k8s);
        if (inventory is not null)
            services.AddSingleton(inventory);
        return new PipelineJobCleanupService(services.BuildServiceProvider(), NullLogger<PipelineJobCleanupService>.Instance);
    }

    [Test]
    public async Task DeletesEveryRoleOfThePipeline_RunningOrFinished_AndNothingElse()
    {
        var hex = Deleted.Id.Value.Value.ToString("N");
        var k8s = new FakeKubernetes();
        var service = Service(new FakeInventory(
            Job("step-running", "step", hex, finished: false),
            Job("step-finished", "step", hex, finished: true),
            Job("build", "build", hex, finished: true),
            Job("map", "map-trigger", hex, finished: false),
            Job("handler", "on-failure", hex, finished: false),
            Job("other-pipeline", "step", OtherHex, finished: false),
            Job("unlabelled", "step", null, finished: false)), k8s);

        await service.HandlePipelineDeletedAsync(Deleted);

        await Assert.That(k8s.DeletedJobs).IsEquivalentTo(["step-running", "step-finished", "build", "map", "handler"]);
    }

    [Test]
    public async Task WithoutKubernetes_OrWhenTheListingFails_NothingThrows()
    {
        var k8s = new FakeKubernetes();

        await Service(null, k8s).HandlePipelineDeletedAsync(Deleted);
        await Service(new RefusingInventory(), k8s).HandlePipelineDeletedAsync(Deleted);

        await Assert.That(k8s.DeletedJobs).IsEmpty();
    }
}
