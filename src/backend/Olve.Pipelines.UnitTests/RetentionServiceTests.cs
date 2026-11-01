using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class RetentionServiceTests
{
    internal sealed class FakeObjects : IRetentionObjectStore
    {
        public List<StoredObject> Objects { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StoredObject>>(Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList());

        public Task<int> DeleteAsync(IReadOnlyList<string> keys, CancellationToken ct)
        {
            Deleted.AddRange(keys);
            Objects.RemoveAll(o => keys.Contains(o.Key));
            return Task.FromResult(0);
        }
    }

    private sealed class FakeInventory(IReadOnlyList<ClusterJob> jobs, IReadOnlyList<ClusterSecret> secrets) : IKubernetesInventory
    {
        public Task<IReadOnlyList<ClusterJob>> ListJobsAsync(string ns, CancellationToken ct = default) => Task.FromResult(jobs);
        public Task<IReadOnlyList<ClusterSecret>> ListSecretsAsync(string ns, CancellationToken ct = default) => Task.FromResult(secrets);
    }

    private sealed record Setup(TestRunDomain D, FakeObjects Objects, Run Old, Run Recent, PipelineVersion Version);

    /// <param name="beforeApply">Runs after the sweep planned and before it applies the plan.</param>
    /// <param name="inventory">The cluster listing; <see cref="Setup"/>'s pipeline id labels its Jobs.</param>
    private static (RetentionService Service, Setup Setup) Build(
        bool dryRun, Action<TestRunDomain, Run>? beforeApply = null,
        Func<Id<Pipeline>, DateTimeOffset, IKubernetesInventory>? inventory = null, IKubernetesClient? kubernetes = null)
    {
        var d = new TestRunDomain().WireEvents();
        var pipeline = d.CreatePipeline();
        var version = d.AddVersion(pipeline.Id);
        var runs = Enumerable.Range(0, 21).Select(_ => d.CreateRun(version)).ToList();
        foreach (var run in runs)
            TestRunDomain.Pick(d.RunService.Complete(run.Id, RunStatus.Succeeded));

        // Everything is "now" in the domain; a 0-day minimum age lets the oldest beyond the latest 20 expire.
        var options = RetentionOptions.Defaults with { DryRun = dryRun, MinRunAge = TimeSpan.Zero };
        var objects = new FakeObjects();
        var hex = pipeline.Id.Value.Value.ToString("N");
        var old = d.Runs.TryGet(runs[0].Id, out var o0) ? o0 : runs[0];
        var recent = d.Runs.TryGet(runs[^1].Id, out var o1) ? o1 : runs[^1];
        DateTimeOffset at = d.Time.GetUtcNow();
        objects.Objects.AddRange(new[]
        {
            new StoredObject($"runs/{hex}/{old.Number}/run.json", at, 1),
            new StoredObject($"runs/{hex}/{old.Number}/build/1/output.json", at, 2),
            new StoredObject($"runs/{hex}/{recent.Number}/build/1/output.json", at, 3),
        });

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(d.Pipelines);
        services.AddSingleton(d.Runs);
        services.AddSingleton(d.Attempts);
        services.AddSingleton(d.Versions);
        services.AddSingleton(d.Activations);
        // Only applying a plan resolves RunService.
        services.AddSingleton(_ =>
        {
            beforeApply?.Invoke(d, old);
            return d.RunService;
        });
        services.AddSingleton(d.SchedulingLock);
        services.AddSingleton<IRetentionObjectStore>(objects);
        if (inventory is not null)
        {
            services.AddSingleton(inventory(pipeline.Id, at));
            services.AddSingleton(new KubernetesOptions("runners"));
            services.AddSingleton(kubernetes!);
        }

        var service = new RetentionService(options, new StorageOptions("b"), services.BuildServiceProvider(), d.Time,
            NullLogger<RetentionService>.Instance);
        return (service, new Setup(d, objects, old, recent, version));
    }

    private static RunStatus StatusOf(TestRunDomain d, Run run) => d.Runs.TryGet(run.Id, out var r) ? r.Status : (RunStatus)(-1);

    [Test]
    public async Task Sweep_DeletesTheExpiredRunThenItsObjects_ButNotRunJson()
    {
        var (service, s) = Build(dryRun: false);

        var plan = await service.SweepAsync(CancellationToken.None);

        await Assert.That(plan.ExpiredRuns.Single().Id).IsEqualTo(s.Old.Id);
        await Assert.That(s.D.Runs.TryGet(s.Old.Id, out _)).IsFalse();
        await Assert.That(s.D.Runs.TryGet(s.Recent.Id, out _)).IsTrue();
        await Assert.That(s.Objects.Deleted).IsEquivalentTo(new[] { $"runs/{s.Old.PipelineId.Value.Value:N}/{s.Old.Number}/build/1/output.json" });
    }

    [Test]
    public async Task DryRun_DeletesNothing()
    {
        var (service, s) = Build(dryRun: true);

        var plan = await service.SweepAsync(CancellationToken.None);

        await Assert.That(plan.ExpiredRuns).Count().IsEqualTo(1);
        await Assert.That(StatusOf(s.D, s.Old)).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(s.Objects.Deleted).IsEmpty();
    }

    [Test]
    public async Task Sweep_KeepsARunResumedAfterItWasPlanned_WithItsObjects()
    {
        var (service, s) = Build(dryRun: false, beforeApply: (d, old) => TestRunDomain.Pick(d.RunService.Reopen(old.Id)));

        var plan = await service.SweepAsync(CancellationToken.None);

        await Assert.That(plan.ExpiredRuns.Single().Id).IsEqualTo(s.Old.Id);
        await Assert.That(StatusOf(s.D, s.Old)).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Objects.Deleted).IsEmpty();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Sweep_DeletesLeakedSecretsAndJobs_WhenTheOtherListIsEmpty(bool secret, bool job)
    {
        var k8s = new FakeKubernetes();
        var (service, _) = Build(dryRun: false, kubernetes: k8s, inventory: (pipeline, now) => new FakeInventory(
            job ? [LeakedJob(pipeline, now)] : [],
            secret ? [new ClusterSecret("olve-map-0123-urls", now.AddHours(-5))] : []));

        await service.SweepAsync(CancellationToken.None);

        await Assert.That(k8s.DeletedSecrets).IsEquivalentTo(secret ? ["olve-map-0123-urls"] : Array.Empty<string>());
        await Assert.That(k8s.DeletedJobs).IsEquivalentTo(job ? ["olve-fh-x"] : Array.Empty<string>());
    }

    private static ClusterJob LeakedJob(Id<Pipeline> pipeline, DateTimeOffset now) => new("olve-fh-x",
        new Dictionary<string, string>
        {
            ["app"] = "olve-pipelines",
            ["olve-role"] = "on-failure",
            ["olve-pipeline"] = pipeline.Value.Value.ToString("N"),
        },
        now.AddDays(-30), Finished: true, TtlSecondsAfterFinished: null);
}
