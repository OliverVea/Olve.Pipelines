using Olve.Pipelines.Pipelines.Configs;
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

    private static string WithStepConfig(JsonObject config, bool clusterAccess = false)
    {
        var node = JsonNode.Parse(Text())!.AsObject();
        var hello = node["stages"]![0]!["steps"]![0]!.AsObject();
        hello["config"] = config;
        if (clusterAccess) hello["clusterAccess"] = true;
        return node.ToJsonString();
    }

    [Test]
    public async Task ConfigRef_ToAFieldOfAReceivedEntry_IsValid()
    {
        var text = WithStepConfig(new JsonObject
        {
            ["host"] = new JsonObject { ["$config"] = "target.host" },
            ["values"] = new JsonArray(new JsonObject { ["auth"] = new JsonObject { ["$config"] = "auth" } }),
            ["mode"] = new JsonObject { ["$config"] = "auth.mode" },
        });

        await Assert.That(ManifestValidator.Validate(Parse(text)).Errors).IsEmpty();
    }

    [Test]
    public async Task ConfigRef_ToAnEntryTheStepDoesNotReceive_OrAMissingField_IsAnError()
    {
        var notReceived = ManifestValidator.Validate(Parse(WithStepConfig(new JsonObject { ["x"] = new JsonObject { ["$config"] = "publish.repository" } })));
        var noField = ManifestValidator.Validate(Parse(WithStepConfig(new JsonObject { ["x"] = new JsonObject { ["$config"] = "target.port" } })));

        await Assert.That(notReceived.Errors.Select(e => e.Code)).IsEquivalentTo([ManifestIssueCodes.ConfigRefNotReceived]);
        await Assert.That(notReceived.Errors.Single().Path).IsEqualTo("stages[0].steps[0].config.x");
        await Assert.That(noField.Errors.Select(e => e.Code)).IsEquivalentTo([ManifestIssueCodes.ConfigRefFieldUnknown]);
        await Assert.That(noField.Errors.Single().Message).Contains("config 'target' has no field 'port'");
    }

    [Test]
    public async Task ClusterAccess_RoundTrips_AndBumpsTheStepVersion()
    {
        var plain = Parse(Text());
        var withAccess = Parse(WithStepConfig(new JsonObject(), clusterAccess: true));
        var written = ManifestWriter.ToCanonicalJson(withAccess);

        await Assert.That(ManifestVersioning.AllSteps(withAccess).Single(s => s.Name == "hello").ClusterAccess).IsTrue();
        await Assert.That(ManifestVersioning.AllSteps(plain).Single(s => s.Name == "hello").ClusterAccess).IsFalse();
        await Assert.That(written).Contains("\"clusterAccess\": true");
        await Assert.That(ManifestWriter.ToCanonicalJson(plain)).DoesNotContain("clusterAccess");
        await Assert.That(ManifestWriter.ToCanonicalJson(Parse(written))).IsEqualTo(written);
    }

    [Test]
    public async Task ClusterAccess_OtherThanTrue_DoesNotParse()
    {
        var node = JsonNode.Parse(Text())!.AsObject();
        node["stages"]![0]!["steps"]![0]!["clusterAccess"] = false;

        await Assert.That(ManifestParser.TryParse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), out _, out _)).IsFalse();
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
