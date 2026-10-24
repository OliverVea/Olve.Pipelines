using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.UnitTests.Manifests;
using static Olve.Pipelines.UnitTests.Configs.ConfigManifests;

namespace Olve.Pipelines.UnitTests.Configs;

public class ManifestConfigTests
{
    [Test]
    public async Task Parses_ConfigEntries_AndStepConfigEntries()
    {
        var manifest = Parse(Text());

        await Assert.That(manifest.Config.Select(c => c.Name)).IsEquivalentTo(["auth", "target"]);
        await Assert.That(manifest.Config.All(c => c.Required)).IsTrue();
        await Assert.That(ManifestVersioning.AllSteps(manifest).Single(s => s.Name == "hello").ConfigEntries).IsEquivalentTo(["auth", "target"]);
        await Assert.That(ManifestVersioning.AllSteps(manifest).Single(s => s.Name == "count").ConfigEntries).IsEmpty();
    }

    [Test]
    public async Task ManifestWithoutConfig_StillParses_AndWritesNoConfigKeys()
    {
        var manifest = TestFixtureManifest.Parse();
        var written = ManifestWriter.ToCanonicalJson(manifest);

        await Assert.That(manifest.Config).IsEmpty();
        await Assert.That(written).DoesNotContain("\"config\"");
        await Assert.That(written).DoesNotContain("configEntries");
    }

    [Test]
    public async Task ConfigManifest_RoundTripsThroughTheWriter()
    {
        var written = ManifestWriter.ToCanonicalJson(Parse(Text()));

        await Assert.That(ManifestWriter.ToCanonicalJson(Parse(written))).IsEqualTo(written);
        await Assert.That(Parse(written).Config.Select(c => c.Name)).IsEquivalentTo(["auth", "target"]);
        await Assert.That(written).Contains("configEntries");
    }

    [Test]
    public async Task Valid_ConfigManifest_HasNoErrors()
        => await Assert.That(ManifestValidator.Validate(Parse(Text())).Errors).IsEmpty();

    [Test]
    public async Task StepReceivingASecretRef_MustDeclareTheSecret()
    {
        var report = ManifestValidator.Validate(Parse(Text(helloDeclaresSecret: false)));

        await Assert.That(report.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.ConfigSecretRefNotInStep);
    }

    [Test]
    public async Task StepUsingAnUndeclaredEntry_AndBadNames_AreErrors()
    {
        var node = JsonNode.Parse(Text())!.AsObject();
        node["stages"]![0]!["steps"]![0]!["configEntries"] = new JsonArray("auth", "nope", "target");
        node["config"]![1]!["name"] = "Target";
        var report = ManifestValidator.Validate(Parse(node.ToJsonString()));

        await Assert.That(report.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.ConfigUnknown);
        await Assert.That(report.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.ConfigNameInvalid);
    }

    [Test]
    public async Task SecretRefToAnUndeclaredSecret_IsAnError()
    {
        var node = JsonNode.Parse(Text())!.AsObject();
        node["secrets"] = new JsonArray();
        node["stages"]![0]!["steps"]![0]!["secrets"] = new JsonArray();
        var report = ManifestValidator.Validate(Parse(node.ToJsonString()));

        await Assert.That(report.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.ConfigSecretRefUndeclared);
    }

    [Test]
    public async Task ChangedEntrySchema_BumpsOnlyTheStepsThatReceiveIt()
    {
        var first = ManifestVersioning.Compute(null, Parse(Text(namespaceDefault: "olve")));
        var second = ManifestVersioning.Compute(first.History, Parse(Text(namespaceDefault: "apps")));

        await Assert.That(second.ChangedSteps).IsEquivalentTo(["hello"]);
        await Assert.That(second.ManifestHash).IsNotEqualTo(first.ManifestHash);
    }
}
