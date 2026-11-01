using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Configs.ConfigManifests;

namespace Olve.Pipelines.UnitTests.Configs;

/// <summary>The activation gate (pending versions) and runs pinning config, over the real domain wiring.</summary>
public class ConfigGateTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Input = """{"sha":"abc1234","mode":"normal"}""";
    private const string NoneConfig = "target: {host: h}\nauth: {mode: none}\n";
    private const string DexConfig = "target: {host: h}\nauth: {mode: dex, clientId: c, clientSecret: {secret: DEX_CLIENT_SECRET}}\n";

    private sealed record World(TestRunDomain D, Pipeline P, PipelineReconcileService Reconcile, ConfigService Config, FakeSecretNames Secrets)
    {
        public IReadOnlySet<string> SetSecrets => Secrets.Set;

        public ReconcileReport Apply(string manifest) => TestRunDomain.Pick(Reconcile.Apply(P.Id, Sha, manifest, SetSecrets));

        public Task<Olve.Results.Result<ConfigSaveResponse>> Save(string yaml) => Config.SaveAsync(P.Id, yaml, "tester", CancellationToken.None);

        public int? ActiveNumber => D.ActivationService.TryGet(P.Id, out var a) && a is PipelineActivation.Active active ? active.VersionNumber : null;
    }

    private static string WithDescription(string description)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(TestFixtureManifest.Text)!.AsObject();
        node["description"] = description;
        return node.ToJsonString();
    }

    private static World Build(HarnessOptions? harness = null)
    {
        var d = new TestRunDomain();
        if (harness is not null) d.Harness = harness;
        d.WireEvents(scheduler: true);
        var p = d.CreatePipeline("provisional");
        var secrets = new FakeSecretNames();
        var reconcile = new PipelineReconcileService(
            factory: null!, control: null!, storage: null!, d.VersionService, d.ActivationService, d.Gate, secrets,
            d.PipelineService, d.EntityVersions, d.SchedulingLock, new HarnessOptions(), NullLogger<PipelineReconcileService>.Instance);
        var config = new ConfigService(d.PipelineService, d.VersionService, d.ActivationService, d.ConfigSnapshots, d.Gate, secrets,
            d.Services, NullLogger<ConfigService>.Instance);
        return new World(d, p, reconcile, config, secrets);
    }

    [Test]
    public async Task FirstVersion_NeedingConfig_IsPending_UntilConfigIsSaved()
    {
        var w = Build();

        var report = w.Apply(Text());

        await Assert.That(report.Pending).IsNotNull();
        await Assert.That(report.Pending!.Reasons).IsEquivalentTo(["config config.auth: is required", "config config.target: is required"]);
        await Assert.That(w.ActiveNumber).IsNull();

        var saved = await w.Save(NoneConfig);

        await Assert.That(saved).Succeeded();
        await Assert.That(TestRunDomain.Pick(saved).ActivatedVersion).IsEqualTo(1);
        await Assert.That(w.ActiveNumber).IsEqualTo(1);
        await Assert.That(w.D.Gate.TryGetPending(w.P.Id, out _)).IsFalse();
    }

    [Test]
    public async Task NewVersion_NeedingConfig_StaysPending_TheActiveVersionStaysActive()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);

        var report = w.Apply(Text());

        await Assert.That(report.Version.Number).IsEqualTo(2);
        await Assert.That(report.Pending).IsNotNull();
        await Assert.That(w.ActiveNumber).IsEqualTo(1);
        await Assert.That(w.D.Health.Get(w.P.Id).Pending).IsNotNull();
        await Assert.That(w.D.Health.Get(w.P.Id).Pending!.Version).IsEqualTo(2);
        await Assert.That(w.D.Health.Get(w.P.Id).Pending!.Held).IsFalse();
    }

    [Test]
    public async Task ConfigFirst_ThenTheSecret_ActivatesThePendingVersion()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        w.Apply(Text());

        var saved = await w.Save(DexConfig);

        await Assert.That(saved).Succeeded();
        await Assert.That(TestRunDomain.Pick(saved).ActivatedVersion).IsNull();
        await Assert.That(TestRunDomain.Pick(saved).Pending!.MissingSecrets).IsEquivalentTo([Secret]);
        await Assert.That(w.D.Health.Get(w.P.Id).Pending!.Reasons).IsEquivalentTo([$"secret {Secret} not set"]);

        w.Secrets.Set.Add(Secret);
        var rechecked = w.D.Gate.Recheck(w.P.Id, w.SetSecrets);

        await Assert.That(TestRunDomain.Pick(rechecked)!.Number).IsEqualTo(2);
        await Assert.That(w.ActiveNumber).IsEqualTo(2);
    }

    [Test]
    public async Task ConfigSavedBeforeTheVersion_ActivatesItAtOnce()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        await Assert.That(await w.Save(NoneConfig)).Succeeded();

        var report = w.Apply(Text());

        await Assert.That(report.Pending).IsNull();
        await Assert.That(w.ActiveNumber).IsEqualTo(2);
    }

    [Test]
    public async Task ActivatingAVersionThatChangesADefault_ReportsItAsAWarning()
    {
        var w = Build();
        w.Apply(Text(namespaceDefault: "olve"));
        TestRunDomain.Pick(await w.Save(NoneConfig));

        var report = w.Apply(Text(namespaceDefault: "apps"));

        await Assert.That(w.ActiveNumber).IsEqualTo(2);
        await Assert.That(report.Warnings).Contains("v2 changes the default of target.namespace (\"olve\" → \"apps\")");
    }

    [Test]
    public async Task SavingADocumentTheActiveVersionRejects_IsRefused()
    {
        var w = Build();
        w.Apply(Text());
        await Assert.That(await w.Save(NoneConfig)).Succeeded();

        var saved = await w.Save("auth: {mode: none}\n");

        await saved.Problems.AreExactly("The document is not valid for the active version 1", "config.target: is required");
        await Assert.That(w.D.ConfigSnapshots.ListByPipeline(w.P.Id).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitSwitch_ToAnUnsatisfiedVersion_IsRefused()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        var pending = w.Apply(Text()).Version;

        var switched = w.D.Gate.Switch(pending, cancelRunning: false, w.SetSecrets);

        await switched.Problems.IsSingle<ConflictProblem>("Version 2 can't be activated: config config.auth: is required; config config.target: is required.");
        await Assert.That(w.ActiveNumber).IsEqualTo(1);
    }

    // Catches: a stale marker left behind by a version that activated, which later reads as held.
    [Test]
    public async Task ANewVersionThatActivates_ClearsThePendingOne()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        w.Apply(Text());

        var report = w.Apply(WithDescription("newer"));

        await Assert.That(report.Pending).IsNull();
        await Assert.That(w.ActiveNumber).IsEqualTo(3);
        await Assert.That(w.D.Gate.TryGetPending(w.P.Id, out _)).IsFalse();
    }

    [Test]
    public async Task ExplicitSwitch_ToASatisfiedVersion_ActivatesIt_AndClearsThePendingOne()
    {
        var w = Build();
        var first = w.Apply(TestFixtureManifest.Text).Version;
        w.Apply(WithDescription("newer"));
        w.Apply(Text());

        var switched = w.D.Gate.Switch(first, cancelRunning: false, w.SetSecrets);

        await Assert.That(switched).Succeeded();
        await Assert.That(w.ActiveNumber).IsEqualTo(1);
        await Assert.That(w.D.Gate.TryGetPending(w.P.Id, out _)).IsFalse();
    }

    // Catches: an equal list of reasons keeping the older marker, so Recheck would activate the older version.
    [Test]
    public async Task ANewerPendingVersion_ReplacesTheOlderOne_EvenForTheSameReasons()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        w.Apply(Text());

        w.Apply(Text(namespaceDefault: "other"));

        await Assert.That(w.D.Gate.TryGetPending(w.P.Id, out var marker)).IsTrue();
        await Assert.That(marker!.VersionNumber).IsEqualTo(3);
    }

    // Catches: the gate ignoring the instance's account, which activates a step that can't work,
    // or holds every such version on an instance that has one.
    [Test]
    [Arguments(false, null, true)]
    [Arguments(false, "olve-deployer", false)]
    [Arguments(true, null, true)]
    [Arguments(true, "olve-deployer", false)]
    public async Task ClusterAccess_IsAskedOfTheInstance_WithOrWithoutASelect(bool select, string? account, bool held)
    {
        var w = Build(new HarnessOptions { StepServiceAccount = account });
        var manifest = (select ? SelectTests.Node() : System.Text.Json.Nodes.JsonNode.Parse(Text())!.AsObject());
        manifest["stages"]![0]!["steps"]![0]!["clusterAccess"] = true;
        var target = select ? "target: {kind: inCluster}" : "target: {host: h}";
        await Assert.That(await w.Save($"{target}\nauth: {{mode: none}}\n")).Succeeded();

        var report = w.Apply(manifest.ToJsonString());

        await Assert.That(report.Pending is not null).IsEqualTo(held);
        if (held) await Assert.That(report.Pending!.Reasons).Contains("step hello needs cluster access, but the instance grants no step ServiceAccount (Kubernetes:StepServiceAccount)");
    }

    // Catches: numbering from anything but the highest, which would overwrite a write-once snapshot and break rollback.
    [Test]
    public async Task EverySave_GetsTheNextSnapshotNumber()
    {
        var w = Build();
        foreach (var host in (string[])["a", "b", "c"])
            await Assert.That(await w.Save($"target: {{host: {host}}}\nauth: {{mode: none}}\n")).Succeeded();

        await Assert.That(w.D.ConfigSnapshots.TryGetLatest(w.P.Id, out var latest)).IsTrue();
        await Assert.That(latest!.Number).IsEqualTo(3);
    }

    [Test]
    public async Task Reconciling_BackToTheActiveVersion_DropsThePendingOne()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        w.Apply(Text());

        w.Apply(TestFixtureManifest.Text);

        await Assert.That(w.D.Gate.TryGetPending(w.P.Id, out _)).IsFalse();
        await Assert.That(w.ActiveNumber).IsEqualTo(1);
    }

    [Test]
    public async Task Runs_PinTheSnapshot_AndTheirEffectiveConfig_LaterSavesDontTouchThem()
    {
        var w = Build();
        w.Apply(Text());
        TestRunDomain.Pick(await w.Save(NoneConfig));
        var first = StartedRun(w);

        TestRunDomain.Pick(await w.Save("target: {host: h2}\nauth: {mode: none}\n"));
        var second = StartedRun(w);

        await Assert.That(first.ConfigSnapshot).IsEqualTo(1);
        await Assert.That(first.Config).IsEqualTo("""{"auth":{"mode":"none"},"target":{"host":"h","namespace":"olve"}}""");
        await Assert.That(Run(w, first).Config).IsEqualTo(first.Config);
        await Assert.That(second.ConfigSnapshot).IsEqualTo(2);
        await Assert.That(second.Config!).Contains("h2");
        await Assert.That(w.D.Views.Summarize(second).ConfigSnapshot).IsEqualTo(2);
    }

    [Test]
    public async Task Attempts_ReceiveTheirStepsSliceOfTheRunsConfig_UnderDollarConfig()
    {
        var w = Build();
        w.Apply(Text());
        TestRunDomain.Pick(await w.Save(NoneConfig));
        var run = StartedRun(w);
        TestRunDomain.Pick(await w.Save("target: {host: later}\nauth: {mode: none}\n"));

        var hello = w.D.AttemptService.ListByRun(run.Id).Single(a => a.StepName == "hello");
        var spec = TestRunDomain.Pick(w.D.Launches.Get(hello.Id));

        await Assert.That(spec.InputJson).Contains("""
            "$config":{"auth":{"mode":"none"},"target":{"host":"h","namespace":"olve"}}
            """.Trim());
        await Assert.That(hello.Input!).DoesNotContain("$config");
    }

    [Test]
    public async Task ContinuingARun_KeepsItsSnapshot()
    {
        var w = Build();
        w.Apply(Text());
        TestRunDomain.Pick(await w.Save(NoneConfig));
        var run = StartedRun(w);
        var hello = w.D.AttemptService.ListByRun(run.Id).Single(a => a.StepName == "hello");
        TestRunDomain.Pick(w.D.AttemptService.MarkInProgress(hello.Id));
        await Assert.That(w.D.AttemptService.MarkFailed(hello.Id, "boom")).Succeeded();
        TestRunDomain.Pick(await w.Save("target: {host: later}\nauth: {mode: none}\n"));

        var continued = w.D.Continuations.ContinueRun(run.Id, Input);

        await Assert.That(TestRunDomain.Pick(continued).ConfigSnapshot).IsEqualTo(1);
        await Assert.That(Run(w, run).Config!).DoesNotContain("later");
    }

    [Test]
    public async Task DeletedReferencedSecret_HoldsTheActiveVersion_NewRunsWait()
    {
        var w = Build();
        w.Secrets.Set.Add(Secret);
        w.Apply(Text());
        TestRunDomain.Pick(await w.Save(DexConfig));
        await Assert.That(w.ActiveNumber).IsEqualTo(1);

        w.Secrets.Set.Clear();
        await Assert.That(w.D.Gate.ReferencingVersion(w.P.Id, Secret, w.SetSecrets)).IsEqualTo(1);
        TestRunDomain.Pick(w.D.Gate.Recheck(w.P.Id, w.SetSecrets));

        await Assert.That(w.D.Gate.Held(w.P.Id)).IsNotNull();
        var start = w.D.Starts.Start(w.P.Id, Input, new RunTrigger(RunTriggerKind.Manual));
        await start.Problems.IsSingle<ConflictProblem>("Version 1 is held until its config is satisfied: secret DEX_CLIENT_SECRET not set.");
        await Assert.That(Problems(start)).Contains($"secret {Secret} not set");

        w.Secrets.Set.Add(Secret);
        TestRunDomain.Pick(w.D.Gate.Recheck(w.P.Id, w.SetSecrets));
        await Assert.That(w.D.Starts.Start(w.P.Id, Input, new RunTrigger(RunTriggerKind.Manual))).Succeeded();
    }

    [Test]
    public async Task VersionWithoutConfig_PinsNothing()
    {
        var w = Build();
        w.Apply(TestFixtureManifest.Text);
        TestRunDomain.Pick(await w.Save(NoneConfig));

        var run = StartedRun(w);

        await Assert.That(run.ConfigSnapshot).IsNull();
        await Assert.That(run.Config).IsNull();
    }

    private static Run StartedRun(World w)
    {
        var outcome = TestRunDomain.Pick(w.D.Starts.Start(w.P.Id, Input, new RunTrigger(RunTriggerKind.Manual)));
        return ((Olve.Pipelines.Pipelines.Scheduling.RunStartOutcome.Started)outcome).Run;
    }

    private static Run Run(World w, Run run) => w.D.RunService.TryGet(run.Id, out var current) ? current : run;
}
