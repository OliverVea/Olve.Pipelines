using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class LeftoverJobCleanupServiceTests
{
    private static readonly DateTimeOffset ProcessStart = DateTimeOffset.UnixEpoch.AddDays(100);
    private static readonly DateTimeOffset Before = ProcessStart.AddHours(-1);
    private static readonly DateTimeOffset After = ProcessStart.AddSeconds(1);

    private sealed class FakeInventory(params ClusterJob[] jobs) : IKubernetesInventory
    {
        public Task<IReadOnlyList<ClusterJob>> ListJobsAsync(string ns, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClusterJob>>(jobs);

        public Task<IReadOnlyList<ClusterSecret>> ListSecretsAsync(string ns, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ClusterJob Job(string name, Id<Pipeline> pipeline, DateTimeOffset created, bool finished = false, string role = "step") => new(
        name,
        new Dictionary<string, string> { ["olve-role"] = role, ["olve-pipeline"] = pipeline.Value.Value.ToString("N") },
        created, finished, 604800);

    private static string Hex() => $"olve-{Guid.NewGuid():N}";

    [Test]
    public async Task OnlyUnfinishedStepJobsNoLiveAttemptOwns_AreLeftovers()
    {
        var s = new SchedulerScenario(TestFixtureManifest.Text);
        var ended = s.Latest(s.Start("""{"sha":"abcdef2","mode":"normal"}"""), "hello")!;
        s.D.AttemptService.Cancel(ended.Id).DiscardResult();
        var live = s.Latest(s.Start("""{"sha":"abcdef1","mode":"normal"}"""), "hello")!;
        var pipeline = s.Pipeline.Id;
        var otherInstance = new IdProvider().Create<Pipeline>();
        var orphan = Hex();

        var leftovers = LeftoverJobCleanupService.Leftovers(
            [
                Job(live.KubernetesJobName, pipeline, Before),
                Job(ended.KubernetesJobName, pipeline, Before),
                Job(orphan, pipeline, Before),
                Job(Hex(), pipeline, After),
                Job(Hex(), pipeline, Before, finished: true),
                Job(Hex(), pipeline, Before, role: "build"),
                Job(Hex(), otherInstance, Before),
            ],
            new HashSet<Id<Pipeline>> { pipeline }, s.D.Attempts.List(), ProcessStart);

        await Assert.That(leftovers.Select(j => j.Name)).IsEquivalentTo([ended.KubernetesJobName, orphan]);
    }

    [Test]
    public async Task DeletesTheJobAndItsUrlSecret()
    {
        var s = new SchedulerScenario(TestFixtureManifest.Text);
        var hex = Guid.NewGuid().ToString("N");
        var k8s = new FakeKubernetes();
        var service = new LeftoverJobCleanupService(
            new FakeInventory(Job($"olve-{hex}", s.Pipeline.Id, Before)), k8s, new KubernetesOptions("runners"),
            s.D.Pipelines, s.D.Attempts, TimeProvider.System, NullLogger<LeftoverJobCleanupService>.Instance);

        await service.DeleteLeftoversAsync(CancellationToken.None);

        await Assert.That(k8s.DeletedJobs).IsEquivalentTo([$"olve-{hex}"]);
        await Assert.That(k8s.DeletedSecrets).IsEquivalentTo([$"olve-urls-{hex}"]);
    }
}
