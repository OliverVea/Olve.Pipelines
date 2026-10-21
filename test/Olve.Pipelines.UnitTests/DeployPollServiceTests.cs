using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class DeployPollServiceTests
{
    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    private sealed class FakeReconciler : IConfigReconciler
    {
        public List<string> Calls { get; } = [];
        public Func<string, Result<ReconcileReport>>? Outcome { get; set; }
        public TimeSpan Delay { get; set; }

        public async Task<Result<ReconcileReport>> ReconcileAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct)
        {
            lock (Calls) Calls.Add(commitSha);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return Outcome?.Invoke(commitSha) ?? Report();
        }

        public static ReconcileReport Report() => new(
            new PipelineVersion(Id.New<PipelineVersion>(), Id.New<Pipeline>(), 1, "{}", "sha256:x", "c", "k",
                new Dictionary<string, int>(), new Dictionary<string, int>(), DateTimeOffset.UnixEpoch),
            Created: true, Warnings: ["[W] stages: unused output"], Secrets: [new SecretDeclaration("TOKEN", "a token")], [], []);
    }

    private sealed class FakePush : IPushTriggerHandler
    {
        public List<string> Fired { get; } = [];

        public Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct)
        {
            lock (Fired) Fired.Add(headSha);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed record Ctx(ServiceProvider Provider, FakeConfigSource Source, FakeReconciler Reconciler, FakePush Push,
        PipelineConfigBindingService Bindings, PipelineConfigBinding Binding, DeployPollService Poll) : IAsyncDisposable
    {
        public PipelineConfigBinding Current => Pick(Bindings.TryGet(Binding.Id));
        public Task Run(bool force = false) => Poll.ReconcileNowAsync(Binding.PipelineId, CancellationToken.None, force);
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private static Ctx Build()
    {
        var source = new FakeConfigSource();
        var reconciler = new FakeReconciler();
        var push = new FakePush();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        services.AddSingleton<IConfigSource>(source);
        services.AddSingleton<IConfigReconciler>(reconciler);
        services.AddSingleton<IPushTriggerHandler>(push);
        var provider = services.BuildServiceProvider();

        var pipeline = Pick(provider.GetRequiredService<PipelineService>().Create("widgets"));
        var bindings = provider.GetRequiredService<PipelineConfigBindingService>();
        var binding = Pick(bindings.Create(pipeline.Id, "acme/widgets", "main", ".pipelines", null));
        return new Ctx(provider, source, reconciler, push, bindings, binding, provider.GetRequiredService<DeployPollService>());
    }

    [Test]
    public async Task NewConfigCommit_IsReconciled_AndRecordedAsSuccess()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";

        await c.Run();

        await Assert.That(c.Reconciler.Calls).IsEquivalentTo(new[] { "cfg-1" });
        var binding = c.Current;
        await Assert.That(binding.LastSyncedSha).IsEqualTo("cfg-1");
        await Assert.That(binding.Status.Result).IsEqualTo(ReconcileResult.Success);
        await Assert.That(binding.Status.Warnings!.Count).IsEqualTo(1);
        await Assert.That(binding.Status.DeclaredSecrets.Single().Name).IsEqualTo("TOKEN");
        await Assert.That(binding.Status.InProgress).IsFalse();
    }

    [Test]
    public async Task AppliedConfigCommit_IsNotRebuilt()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";

        await c.Run();
        await c.Run(force: true); // even a forced fetch: the commit is already applied

        await Assert.That(c.Reconciler.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FailedReconcile_RecordsProblems_HoldsRuns_AndIsRetriedOnlyWhenForced()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-bad";
        c.Reconciler.Outcome = _ => Result.Failure<ReconcileReport>(new ResultProblem("[E1] stages[0]: broken"));
        await c.Run(); // seeds nothing: config failed first
        c.Source.BranchHeadSha = "head-1";

        await c.Run();

        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Error);
        await Assert.That(c.Current.Status.Problems.Single()).Contains("broken");
        await Assert.That(c.Current.LastDeployedSha).IsNull(); // runs held while the config commit is unapplied
        await Assert.That(c.Reconciler.Calls.Count).IsEqualTo(1);

        c.Reconciler.Outcome = null;
        await c.Run(force: true);

        await Assert.That(c.Reconciler.Calls.Count).IsEqualTo(2);
        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Success);
        await Assert.That(c.Current.LastDeployedSha).IsEqualTo("head-1");
    }

    [Test]
    public async Task FirstHead_SeedsCursor_ThenAnAdvanceFiresThePushTrigger()
    {
        await using var c = Build();
        c.Source.BranchHeadSha = "head-0";

        await c.Run();
        await Assert.That(c.Push.Fired).IsEmpty();
        await Assert.That(c.Current.LastDeployedSha).IsEqualTo("head-0");

        c.Source.BranchHeadSha = "head-1";
        await c.Run();

        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1" });
        await Assert.That(c.Current.LastDeployedSha).IsEqualTo("head-1");
    }

    /// <summary>
    /// #39 and v2 ordering: the webhook, the background loop and reconcile-now can poll one binding at
    /// once. The per-binding gate serialises them: one reconcile, one push trigger.
    /// </summary>
    [Test]
    public async Task ConcurrentPolls_ReconcileAndFireOnce()
    {
        await using var c = Build();
        await c.Run(); // seed cursor at head-0, apply config-0
        c.Source.ConfigSha = "cfg-1";
        c.Source.ConfigETag = "etag-1";
        c.Source.BranchHeadSha = "head-1";
        c.Reconciler.Delay = TimeSpan.FromMilliseconds(100);

        await Task.WhenAll(c.Run(), c.Run(), c.Run());

        await Assert.That(c.Reconciler.Calls.Count(s => s == "cfg-1")).IsEqualTo(1);
        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1" });
    }
}
