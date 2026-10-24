using Olve.Pipelines.Pipelines.Configs;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.UnitTests.Configs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>The domain half of reconcile: manifest text → validated version → switch.</summary>
public class ReconcileApplyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static (TestRunDomain D, Pipeline P, PipelineReconcileService Service) Build()
    {
        var d = new TestRunDomain().WireEvents(scheduler: true);
        var p = d.CreatePipeline("provisional");
        var service = new PipelineReconcileService(
            factory: null!, control: null!, storage: null!, d.VersionService, d.ActivationService, d.Gate, new FakeSecretNames(),
            d.PipelineService, d.EntityVersions, d.SchedulingLock, new HarnessOptions(),
            NullLogger<PipelineReconcileService>.Instance);
        return (d, p, service);
    }

    private static string WithHandlerHash(string step, string hash)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(TestFixtureManifest.Text)!.AsObject();
        foreach (var stage in node["stages"]!.AsArray())
        {
            var steps = stage!["steps"] ?? stage["stages"]![0]!["steps"];
            foreach (var s in steps!.AsArray())
                if ((string?)s!["name"] == step) s["handlerHash"] = hash;
        }
        return node.ToJsonString();
    }

    [Test]
    public async Task FirstManifest_AddsVersion1_SwitchesToIt_AndNamesThePipeline()
    {
        var (d, p, service) = Build();

        var report = service.Apply(p.Id, Sha, TestFixtureManifest.Text);

        await Assert.That(report).Succeeded();
        var version = report.Value!.Version;
        await Assert.That(version.Number).IsEqualTo(1);
        await Assert.That(report.Value.Created).IsTrue();
        await Assert.That(version.BinaryKey).IsEqualTo(StorageLayout.BuildBinaryKey(p.Id, Sha));
        await Assert.That(version.CommitSha).IsEqualTo(Sha);
        await Assert.That(version.StepVersions["hello"]).IsEqualTo(1);
        d.ActivationService.TryGet(p.Id, out var activation);
        await Assert.That(((PipelineActivation.Active)activation!).VersionId).IsEqualTo(version.Id);
        d.PipelineService.TryGet(p.Id, out var pipeline);
        await Assert.That(pipeline!.Name).IsEqualTo("v2 test fixture");
        await Assert.That(pipeline.Description!).Contains("fixture");
        await Assert.That(d.EntityVersions.TryGet(p.Id, out _)).IsTrue();
    }

    [Test]
    public async Task SameManifest_AddsNoVersion()
    {
        var (d, p, service) = Build();
        service.Apply(p.Id, Sha, TestFixtureManifest.Text).DiscardResult();

        var again = service.Apply(p.Id, new string('b', 40), TestFixtureManifest.Text);

        await Assert.That(again).Succeeded();
        await Assert.That(again.Value!.Created).IsFalse();
        await Assert.That(d.VersionService.ListByPipeline(p.Id).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ChangedHandler_AddsVersion2_AndBumpsOnlyThatStep()
    {
        var (d, p, service) = Build();
        service.Apply(p.Id, Sha, TestFixtureManifest.Text).DiscardResult();

        var report = service.Apply(p.Id, new string('c', 40), WithHandlerHash("count", "sha256:" + new string('9', 64)));

        await Assert.That(report).Succeeded();
        var v2 = report.Value!.Version;
        await Assert.That(v2.Number).IsEqualTo(2);
        await Assert.That(v2.StepVersions["count"]).IsEqualTo(2);
        await Assert.That(v2.StepVersions["hello"]).IsEqualTo(1);
        await Assert.That(report.Value.ChangedSteps).IsEquivalentTo(new[] { "count" });
        d.ActivationService.TryGet(p.Id, out var activation);
        await Assert.That(((PipelineActivation.Active)activation!).VersionNumber).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidManifest_IsRejected_WithoutAVersion()
    {
        var (d, p, service) = Build();
        var node = System.Text.Json.Nodes.JsonNode.Parse(TestFixtureManifest.Text)!.AsObject();
        node["input"] = "NoSuchModel";

        var report = service.Apply(p.Id, Sha, node.ToJsonString());

        await Assert.That(report).Failed();
        await Assert.That(d.VersionService.ListByPipeline(p.Id)).IsEmpty();
    }

    [Test]
    public async Task UnparseableManifest_IsRejected()
    {
        var (_, p, service) = Build();

        await Assert.That(service.Apply(p.Id, Sha, "{\"apiVersion\":\"2.0\"}")).Failed();
    }
}
