using Olve.Pipelines.Pipelines.Ci;
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
                Job(CiBuild.JobName(pipeline, new string('a', 40)), pipeline, Before, role: "ci-build"),
                Job(Hex(), otherInstance, Before),
            ],
            new HashSet<Id<Pipeline>> { pipeline }, s.D.Attempts.List(), ProcessStart);

        await Assert.That(leftovers.Select(j => j.Name)).IsEquivalentTo([ended.KubernetesJobName, orphan]);
    }

    // Catches: a Job owned by an attempt of another kind than CD's deleted at startup as an orphan.
    [Test]
    public async Task AJobAnyKindOfAttemptOwns_IsJudgedByThatAttempt()
    {
        var pipeline = new IdProvider().Create<Pipeline>();
        var live = Hex();
        var ended = Hex();

        var leftovers = LeftoverJobCleanupService.Leftovers(
            [Job(live, pipeline, Before), Job(ended, pipeline, Before)],
            new HashSet<Id<Pipeline>> { pipeline },
            new Dictionary<string, bool> { [live] = true, [ended] = false },
            ProcessStart);

        await Assert.That(leftovers.Select(j => j.Name)).IsEquivalentTo([ended]);
    }

    // Catches: a restart deleting the Jobs of running CI attempts, or leaving a finished CI attempt's Job behind.
    [Test]
    public async Task CiJobs_AreJudgedByTheirCiAttempts()
    {
        var pipeline = new IdProvider().Create<Pipeline>();
        string Ci() => $"{CiAttempt.JobPrefix}{Guid.NewGuid():N}";
        var live = Ci();
        var ended = Ci();
        var orphan = Ci();

        var leftovers = LeftoverJobCleanupService.Leftovers(
            [Job(live, pipeline, Before, role: "ci-step"), Job(ended, pipeline, Before, role: "ci-step"), Job(orphan, pipeline, Before, role: "ci-step"),
                Job(Ci(), pipeline, After, role: "ci-step")],
            new HashSet<Id<Pipeline>> { pipeline },
            new Dictionary<string, bool> { [live] = true, [ended] = false },
            ProcessStart);

        await Assert.That(leftovers.Select(j => j.Name)).IsEquivalentTo([ended, orphan]);
    }

    [Test]
    [Arguments("olve-0123", "olve-urls-0123")]
    [Arguments("olve-0123-preflight", "olve-urls-0123-preflight")]
    [Arguments("olve-ci-0123", "olve-ci-0123-urls")]
    public async Task A_jobs_url_secret_follows_its_kind(string job, string secret) =>
        await Assert.That(LeftoverJobCleanupService.UrlSecretOf(job)).IsEqualTo(secret);

    [Test]
    public async Task DeletesTheJobAndItsUrlSecret()
    {
        var s = new SchedulerScenario(TestFixtureManifest.Text);
        var hex = Guid.NewGuid().ToString("N");
        var k8s = new FakeKubernetes();
        var service = new LeftoverJobCleanupService(
            new FakeInventory(Job($"olve-{hex}", s.Pipeline.Id, Before)), k8s, new KubernetesOptions("runners"),
            s.D.Pipelines, s.D.Attempts, new EntityStore<CiAttempt>([]), TimeProvider.System, NullLogger<LeftoverJobCleanupService>.Instance);

        await service.DeleteLeftoversAsync(CancellationToken.None);

        await Assert.That(k8s.DeletedJobs).IsEquivalentTo([$"olve-{hex}"]);
        await Assert.That(k8s.DeletedSecrets).IsEquivalentTo([$"olve-urls-{hex}"]);
    }
}
