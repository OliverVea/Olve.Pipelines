using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class DeployPollServiceTests
{
    private static T Pick<T>(Result<T> r) { r.TryPickProblems(out _, out var v); return v!; }

    private sealed class FakeReconciler : IConfigReconciler
    {
        public List<string> Calls { get; } = [];
        public Func<string, Result<ReconcileReport>>? Outcome { get; set; }

        /// <summary>When set, a reconcile holds here until the test completes it.</summary>
        public TaskCompletionSource? Held { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<ReconcileReport>> ReconcileAsync(PipelineConfigBinding binding, string commitSha, CancellationToken ct)
        {
            lock (Calls) Calls.Add(commitSha);
            Entered.TrySetResult();
            if (Held is { } held) await held.Task.WaitAsync(ct);
            return Outcome?.Invoke(commitSha) ?? Report();
        }

        public static ReconcileReport Report() => new(
            new PipelineVersion(Id.New<PipelineVersion>(), Id.New<Pipeline>(), 1, "{}", "sha256:x", "c", "k",
                new Dictionary<string, int>(), new Dictionary<string, int>(), DateTimeOffset.UnixEpoch),
            Created: true, Warnings: ["[W] stages: unused output"], [], []);
    }

    private sealed class FakePush : IPushTriggerHandler
    {
        public List<string> Fired { get; } = [];
        public List<string?> PushShas { get; } = [];
        public bool Fail { get; set; }

        public Task<Result> FireAsync(PipelineConfigBinding binding, string headSha, CancellationToken ct, Olve.Pipelines.Pipelines.Runs.RunOrigin? origin = null)
        {
            lock (Fired)
            {
                Fired.Add(headSha);
                PushShas.Add(origin?.PushSha);
            }
            return Task.FromResult(Fail ? Result.Failure(new ResultProblem("map-trigger Job failed")) : Result.Success());
        }
    }

    private sealed record Ctx(ServiceProvider Provider, FakeConfigSource Source, FakeReconciler Reconciler, FakePush Push,
        PipelineConfigBindingService Bindings, PipelineConfigBinding Binding, DeployPollService Poll, PushTriggerService Pushes) : IAsyncDisposable
    {
        public PipelineConfigBinding Current => Pick(Bindings.TryGet(Binding.Id));
        /// <summary>One config-then-push check, as the push source and the binding webhook run it.</summary>
        public Task Run(bool force = false) => Pushes.CheckAsync(Binding.PipelineId, CancellationToken.None, force);
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
        return new Ctx(provider, source, reconciler, push, bindings, binding,
            provider.GetRequiredService<DeployPollService>(), provider.GetRequiredService<PushTriggerService>());
    }

    [Test]
    public async Task NewConfigCommit_IsReconciled_AndRecordedAsSuccess()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";

        await c.Run();

        await Assert.That(c.Reconciler.Calls).IsEquivalentTo(new[] { "cfg-1" });
        var binding = c.Current;
        await Assert.That(binding.LastReconciledSha).IsEqualTo("cfg-1");
        await Assert.That(binding.Status.Result).IsEqualTo(ReconcileResult.Success);
        await Assert.That(binding.Status.Warnings!.Count).IsEqualTo(1);
        await Assert.That(binding.Status.VersionNumber).IsEqualTo(1);
        await Assert.That(binding.Status.InProgress).IsFalse();
    }

    [Test]
    public async Task BindingWithPollingOff_IsNotPolled_ButStillReconcilesWhenAsked()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        await Assert.That(c.Poll.Sources().Count()).IsEqualTo(1);

        Pick(c.Bindings.SetPolls(c.Binding.PipelineId, false));

        await Assert.That(c.Poll.Sources()).IsEmpty();
        await Assert.That(c.Pushes.Sources()).IsEmpty();
        await Assert.That(await c.Poll.ReconcileNowAsync(c.Binding.PipelineId, CancellationToken.None)).Succeeded();
        await Assert.That(c.Reconciler.Calls).IsEquivalentTo(new[] { "cfg-1" });
    }

    // Catches: without a webhook, pushes that would never trigger because no source polls for them.
    [Test]
    [Arguments(true, 1)]
    [Arguments(false, 0)]
    public async Task APollingBinding_GetsAPushSource_OnlyWhenThePipelineHasOnPush(bool onPush, int sources)
    {
        await using var c = Build();
        var manifest = SchedulerScenario.Mutated(r => { if (!onPush) r["triggers"]!.AsArray().RemoveAt(0); });
        var version = Pick(c.Provider.GetRequiredService<PipelineVersionService>().Add(
            c.Binding.PipelineId, manifest, "sha256:x", null, new Dictionary<string, int>(), new Dictionary<string, int>()));
        Pick(c.Provider.GetRequiredService<SwitchService>().Switch(c.Binding.PipelineId, version.Id));

        await Assert.That(c.Pushes.Sources().Select(source => source.Key)).IsEquivalentTo(Enumerable.Repeat($"push:{c.Binding.Id}", sources));
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
    public async Task AppliedConfigCommit_IsReconciledAgain_WhenARebuildIsAsked()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";

        await c.Run();
        await Assert.That(await c.Poll.ReconcileNowAsync(c.Binding.PipelineId, CancellationToken.None, rebuild: true)).Succeeded();

        await Assert.That(c.Reconciler.Calls).IsEquivalentTo(new[] { "cfg-1", "cfg-1" });
        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Success);
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
        await Assert.That(c.Current.Status.Problems.Single()).IsEqualTo("[E1] stages[0]: broken");
        await Assert.That(c.Current.LastPushSha).IsNull(); // runs held while the config commit is unapplied
        await Assert.That(c.Reconciler.Calls.Count).IsEqualTo(1);

        c.Reconciler.Outcome = null;
        await c.Run(force: true);

        await Assert.That(c.Reconciler.Calls.Count).IsEqualTo(2);
        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Success);
        await Assert.That(c.Current.LastPushSha).IsEqualTo("head-1");
    }

    [Test]
    public async Task FirstHead_SeedsCursor_ThenAnAdvanceFiresThePushTrigger()
    {
        await using var c = Build();
        c.Source.BranchHeadSha = "head-0";

        await c.Run();
        await Assert.That(c.Push.Fired).IsEmpty();
        await Assert.That(c.Current.LastPushSha).IsEqualTo("head-0");

        c.Source.BranchHeadSha = "head-1";
        await c.Run();

        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1" });
        await Assert.That(c.Push.PushShas).IsEquivalentTo(new string?[] { "head-1" });
        await Assert.That(c.Current.LastPushSha).IsEqualTo("head-1");
    }

    [Test]
    public async Task CommitPushedWhileTheConfigBuilds_WaitsForItsOwnConfig()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-0";
        c.Source.BranchHeadSha = "head-0";
        await c.Run();

        c.Source.ConfigSha = "head-1";
        c.Source.ConfigETag = "etag-1";
        c.Source.BranchHeadSha = "head-1";
        c.Reconciler.Outcome = sha =>
        {
            if (sha == "head-1")
            {
                // head-2, with config of its own, lands while head-1's config builds.
                c.Source.ConfigSha = "head-2";
                c.Source.ConfigETag = "etag-2";
                c.Source.BranchHeadSha = "head-2";
            }
            return FakeReconciler.Report();
        };
        await c.Run();
        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1" });

        await c.Run();
        await Assert.That(c.Reconciler.Calls).IsEquivalentTo(new[] { "cfg-0", "head-1", "head-2" });
        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1", "head-2" });
    }

    [Test]
    public async Task FailedFire_KeepsTheCursor_SoTheNextCheckFiresAgain()
    {
        await using var c = Build();
        c.Source.BranchHeadSha = "head-0";
        await c.Run();

        c.Source.BranchHeadSha = "head-1";
        c.Push.Fail = true;
        await c.Run();
        await Assert.That(c.Current.LastPushSha).IsEqualTo("head-0");

        c.Push.Fail = false;
        await c.Run();
        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1", "head-1" });
        await Assert.That(c.Current.LastPushSha).IsEqualTo("head-1");
    }

    /// <summary>
    /// #39 and v2 ordering: the webhook, the background loop and reconcile-now can poll one binding at
    /// once. The per-binding lock serialises them: one reconcile, one push trigger.
    /// </summary>
    [Test]
    public async Task ConcurrentPolls_ReconcileAndFireOnce()
    {
        await using var c = Build();
        await c.Run(); // seed cursor at head-0, apply config-0
        c.Source.ConfigSha = "cfg-1";
        c.Source.ConfigETag = "etag-1";
        c.Source.BranchHeadSha = "head-1";
        c.Reconciler.Held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var polls = new[] { c.Run(), c.Run(), c.Run() };
        await c.Reconciler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        c.Reconciler.Held.SetResult();
        await Task.WhenAll(polls).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(c.Reconciler.Calls.Count(s => s == "cfg-1")).IsEqualTo(1);
        await Assert.That(c.Push.Fired).IsEquivalentTo(new[] { "head-1" });
    }

    [Test]
    public async Task ConfigGate_ReconcilesAPendingConfigCommit_BeforeRunningTheAction()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        var order = new List<string>();
        c.Reconciler.Outcome = sha => { order.Add($"reconcile {sha}"); return FakeReconciler.Report(); };

        var ran = await c.Poll.RunIfConfigCurrentAsync(c.Binding.PipelineId, () => { order.Add("action"); return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(ran).IsTrue();
        await Assert.That(order).IsEquivalentTo(new[] { "reconcile cfg-1", "action" });
        await Assert.That(c.Current.LastReconciledSha).IsEqualTo("cfg-1");
    }

    // Catches: a sync that keeps the binding's lock, after which polling, reconcile-now and every automatic
    // run start wait forever and that pipeline's deploys stop silently. The fakes complete synchronously, so
    // only a held lock can leave these tasks pending: no timeout, and a regression fails instead of hanging.
    [Test]
    [Arguments("applied")]
    [Arguments("failed")]
    [Arguments("cancelled")]
    public async Task ASync_ReleasesTheBinding_HoweverItEnds(string ending)
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        c.Reconciler.Outcome = ending switch
        {
            "failed" => _ => Result.Failure<ReconcileReport>(new ResultProblem("broken")),
            "cancelled" => _ => throw new OperationCanceledException(),
            _ => null,
        };
        var first = c.Poll.ReconcileNowAsync(c.Binding.PipelineId, CancellationToken.None);
        await Assert.That(first.IsCompleted).IsTrue();
        await Assert.That(first.IsCanceled).IsEqualTo(ending == "cancelled");
        await Assert.That(c.Current.Status.InProgress).IsFalse();
        c.Reconciler.Outcome = null;

        var again = c.Poll.ReconcileNowAsync(c.Binding.PipelineId, CancellationToken.None);
        await Assert.That(again.IsCompleted).IsTrue();
        await Assert.That(await again).Succeeded();

        var ran = false;
        var gate = c.Poll.RunIfConfigCurrentAsync(c.Binding.PipelineId, () => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        await Assert.That(gate.IsCompleted).IsTrue();
        await Assert.That(ran).IsTrue();
    }

    [Test]
    public async Task FailedConfigFetch_RecordsTheProblem_HoldsRuns_AndClearsOnTheNextFetch()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        c.Source.FetchOverride = Result.Failure<ConfigFetch>(new ResultProblem("GitHub answered 502"));

        await c.Run();

        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Error);
        await Assert.That(c.Current.Status.LastSyncTime).IsNotNull();
        await Assert.That(c.Current.Status.Problems.Single()).IsEqualTo("GitHub answered 502");
        await Assert.That(c.Reconciler.Calls).IsEmpty();
        await Assert.That(c.Current.LastPushSha).IsNull();

        c.Source.FetchOverride = null;
        await c.Run();

        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Success);
        await Assert.That(c.Current.Status.Problems).IsEmpty();
    }

    // The UI shows a reconcile as running from this status alone.
    [Test]
    public async Task ReconcileInProgress_IsShownWithItsSha_UntilItEnds()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Reconciler.Held = held;

        var run = c.Run();
        await c.Reconciler.Entered.Task;

        await Assert.That(c.Current.Status.InProgress).IsTrue();
        await Assert.That(c.Current.Status.Sha).IsEqualTo("cfg-1");

        held.SetResult();
        await run;

        await Assert.That(c.Current.Status.InProgress).IsFalse();
    }

    [Test]
    public async Task ThrownReconcile_IsRecordedWithItsMessage()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        c.Reconciler.Outcome = _ => throw new InvalidOperationException("boom");

        await c.Run();

        await Assert.That(c.Current.Status.Result).IsEqualTo(ReconcileResult.Error);
        await Assert.That(c.Current.Status.Problems.Single()).IsEqualTo("Reconcile failed: boom");
        await Assert.That(c.Current.Status.InProgress).IsFalse();
    }

    [Test]
    public async Task ConfigGate_FailedReconcile_DoesNotRunTheAction()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-bad";
        c.Reconciler.Outcome = _ => Result.Failure<ReconcileReport>(new ResultProblem("broken"));
        var ran = false;

        var result = await c.Poll.RunIfConfigCurrentAsync(c.Binding.PipelineId, () => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(result).IsFalse();
        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task ConfigGate_UnboundPipeline_RunsTheActionAtOnce()
    {
        await using var c = Build();
        var ran = false;

        var result = await c.Poll.RunIfConfigCurrentAsync(Id.New<Pipeline>(), () => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(result).IsTrue();
        await Assert.That(ran).IsTrue();
        await Assert.That(c.Reconciler.Calls).IsEmpty();
    }

    [Test]
    public async Task ConfigSync_AppliesTheConfig_ButNeverFiresThePushTrigger()
    {
        await using var c = Build();
        c.Source.ConfigSha = "cfg-1";
        c.Source.BranchHeadSha = "head-0";
        await c.Run(); // seeds the push cursor
        c.Source.ConfigSha = "cfg-2";
        c.Source.BranchHeadSha = "head-1";

        await Assert.That(await c.Poll.ReconcileNowAsync(c.Binding.PipelineId, CancellationToken.None)).Succeeded();

        await Assert.That(c.Current.LastReconciledSha).IsEqualTo("cfg-2");
        await Assert.That(c.Current.LastPushSha).IsEqualTo("head-0");
        await Assert.That(c.Push.Fired).IsEmpty();
    }
}
