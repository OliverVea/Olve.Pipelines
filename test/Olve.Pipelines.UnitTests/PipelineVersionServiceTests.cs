using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Versions.PipelineActivation;

namespace Olve.Pipelines.UnitTests;

public class PipelineVersionServiceTests
{
    [Test]
    public async Task Add_NumbersVersionsPerPipeline_AndDerivesBinaryKey()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline();
        var other = d.CreatePipeline();

        var v1 = d.AddVersion(p.Id, commit: "c1");
        var v2 = d.AddVersion(p.Id, commit: "c2", hash: "sha256:def");
        var o1 = d.AddVersion(other.Id);

        await Assert.That(v1.Number).IsEqualTo(1);
        await Assert.That(v2.Number).IsEqualTo(2);
        await Assert.That(o1.Number).IsEqualTo(1);
        await Assert.That(v2.BinaryKey).IsEqualTo(StorageLayout.VersionBinaryKey(p.Id, 2));
        await Assert.That(v2.CommitSha).IsEqualTo("c2");
        await Assert.That(v2.ManifestHash).IsEqualTo("sha256:def");
    }

    [Test]
    public async Task Add_KeepsManifestVerbatim_AndCopiesEntityVersionMaps()
    {
        var d = new TestRunDomain();
        var steps = new Dictionary<string, int> { ["build"] = 3, ["deploy-beta"] = 2 };
        const string manifest = """{"apiVersion":"2.0","name":"x","stages":[]}""";

        var v = d.AddVersion(d.CreatePipeline().Id, manifest: manifest, steps: steps,
            models: new Dictionary<string, int> { ["BuildArtifact"] = 1 });
        steps["build"] = 99; // the version is immutable: later mutation of the caller's map must not leak in

        await Assert.That(v.Manifest).IsEqualTo(manifest);
        await Assert.That(v.StepVersions["build"]).IsEqualTo(3);
        await Assert.That(v.ModelVersions["BuildArtifact"]).IsEqualTo(1);
    }

    [Test]
    public async Task Add_Validates()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline();
        var empty = new Dictionary<string, int>();

        await Assert.That(d.VersionService.Add(Id.New<Pipeline>(), "{}", "sha256:x", null, empty, empty)).Failed();
        await Assert.That(d.VersionService.Add(p.Id, "{ bad", "sha256:x", null, empty, empty)).Failed();
        await Assert.That(d.VersionService.Add(p.Id, "{}", " ", null, empty, empty)).Failed();
    }

    [Test]
    public async Task Lookups_Latest_ByNumber_List_AndPeek()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline();
        await Assert.That(d.VersionService.TryGetLatest(p.Id, out _)).IsFalse();
        await Assert.That(d.VersionService.PeekNextNumber(p.Id)).IsEqualTo(1);

        var v1 = d.AddVersion(p.Id);
        var v2 = d.AddVersion(p.Id);

        await Assert.That(d.VersionService.TryGetLatest(p.Id, out var latest)).IsTrue();
        await Assert.That(latest!.Id).IsEqualTo(v2.Id);
        await Assert.That(d.VersionService.TryGetByNumber(p.Id, 1, out var first)).IsTrue();
        await Assert.That(first!.Id).IsEqualTo(v1.Id);
        await Assert.That(d.VersionService.ListByPipeline(p.Id).Select(v => v.Number)).IsEquivalentTo(new[] { 2, 1 });
        await Assert.That(d.VersionService.PeekNextNumber(p.Id)).IsEqualTo(3);
    }

    // Version numbers name binaries in storage: a deleted newest version's number is never reused.
    [Test]
    public async Task Add_AfterDeletingNewestVersion_DoesNotReuseItsNumber()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline();
        d.AddVersion(p.Id);
        var v2 = d.AddVersion(p.Id);

        await Assert.That(d.VersionService.Delete(v2.Id).Succeeded).IsTrue();

        await Assert.That(d.AddVersion(p.Id).Number).IsEqualTo(3);
    }
}

public class PipelineActivationServiceTests
{
    private static (TestRunDomain D, Id<Pipeline> P, PipelineVersion V1, PipelineVersion V2, PipelineVersion V3) Setup()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        return (d, p, d.AddVersion(p), d.AddVersion(p), d.AddVersion(p));
    }

    private static PipelineActivation Switch(TestRunDomain d, Id<Pipeline> p, PipelineVersion to, bool cancelRunning = false)
        => TestRunDomain.Pick(d.ActivationService.BeginSwitch(p, to.Id, cancelRunning));

    [Test]
    public async Task FirstSwitch_ActivatesImmediately()
    {
        var (d, p, v1, _, _) = Setup();
        await Assert.That(d.ActivationService.TryGet(p, out _)).IsFalse();

        var state = Switch(d, p, v1);

        await Assert.That(state).IsTypeOf<Active>();
        await Assert.That(((Active)state).VersionNumber).IsEqualTo(1);
        await Assert.That(d.ActivationService.TryGet(p, out var stored)).IsTrue();
        await Assert.That(stored).IsEqualTo(state);
    }

    [Test]
    public async Task SwitchFromActive_Drains_ThenCompletes()
    {
        var (d, p, v1, v2, _) = Setup();
        Switch(d, p, v1);

        var draining = Switch(d, p, v2, cancelRunning: true);
        var completed = TestRunDomain.Pick(d.ActivationService.CompleteSwitch(p));

        await Assert.That(draining).IsTypeOf<Draining>();
        var dr = (Draining)draining;
        await Assert.That(dr.FromVersionId).IsEqualTo(v1.Id);
        await Assert.That(dr.ToVersionId).IsEqualTo(v2.Id);
        await Assert.That(dr.CancelRunning).IsTrue();
        await Assert.That(completed).IsTypeOf<Active>();
        await Assert.That(((Active)completed).VersionId).IsEqualTo(v2.Id);
    }

    [Test]
    public async Task SwitchToActiveVersion_IsNoOp()
    {
        var (d, p, v1, _, _) = Setup();
        var active = Switch(d, p, v1);

        await Assert.That(Switch(d, p, v1)).IsEqualTo(active);
    }

    [Test]
    public async Task SwitchWhileDraining_RetargetsOrRollsBack()
    {
        var (d, p, v1, v2, v3) = Setup();
        Switch(d, p, v1);
        Switch(d, p, v2);

        var retargeted = (Draining)Switch(d, p, v3);
        await Assert.That(retargeted.FromVersionId).IsEqualTo(v1.Id);
        await Assert.That(retargeted.ToVersionId).IsEqualTo(v3.Id);

        var same = (Draining)Switch(d, p, v3, cancelRunning: true);
        await Assert.That(same.CancelRunning).IsTrue();

        var rolledBack = Switch(d, p, v1);
        await Assert.That(rolledBack).IsTypeOf<Active>();
        await Assert.That(((Active)rolledBack).VersionId).IsEqualTo(v1.Id);
    }

    [Test]
    public async Task CompleteSwitch_FailsUnlessDraining()
    {
        var (d, p, v1, _, _) = Setup();
        await Assert.That(d.ActivationService.CompleteSwitch(p)).Failed();

        Switch(d, p, v1);
        await Assert.That(d.ActivationService.CompleteSwitch(p)).Failed();
    }

    [Test]
    public async Task Switch_RejectsVersionOfAnotherPipeline()
    {
        var (d, p, _, _, _) = Setup();
        var foreign = d.AddVersion(d.CreatePipeline().Id);

        await Assert.That(d.ActivationService.BeginSwitch(p, foreign.Id)).Failed();
        await Assert.That(d.ActivationService.BeginSwitch(p, Id.New<PipelineVersion>())).Failed();
    }
}
