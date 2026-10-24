using Olve.Pipelines.Pipelines.Configs;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Configs.ConfigManifests;

namespace Olve.Pipelines.UnitTests.Configs;

public class ConfigValidationTests
{
    private static readonly HashSet<string> NoSecrets = new(StringComparer.Ordinal);
    private static readonly HashSet<string> DexSecretSet = new(StringComparer.Ordinal) { Secret };

    [Test]
    public async Task Effective_AppliesDefaults_KeepsOnlyDeclaredEntries_AndIgnoresUnknownKeys()
    {
        var manifest = Parse(Text());
        var doc = Yaml("target: {host: h}\nauth: {mode: none}\nfuture: {anything: 1}\n");

        await Assert.That(ConfigValidation.Effective(manifest, doc)).SucceededAndValue(v => v.Satisfies(
            e => e!.ToJsonString(), json => json.IsEqualTo("""{"auth":{"mode":"none"},"target":{"host":"h","namespace":"olve"}}""")));
        await Assert.That(doc.ToJsonString()).DoesNotContain("namespace");
    }

    [Test]
    public async Task MissingRequiredEntry_IsAProblem()
    {
        var check = ConfigValidation.Check(Parse(Text()), 9, Yaml("auth: {mode: none}\n"), NoSecrets);

        await Assert.That(check.Satisfied).IsFalse();
        await Assert.That(check.Reasons).IsEquivalentTo(["config config.target: is required"]);
    }

    [Test]
    public async Task DiscriminatedUnion_ReportsOnlyTheChosenBranch()
    {
        var check = ConfigValidation.Check(Parse(Text()), 9, Yaml("target: {host: h}\nauth: {mode: dex}\n"), NoSecrets);

        await Assert.That(check.Problems).IsEquivalentTo(["config.auth.clientId: is required", "config.auth.clientSecret: is required"]);
    }

    [Test]
    public async Task UnknownDiscriminator_ListsTheShapes()
    {
        var check = ConfigValidation.Check(Parse(Text()), 9, Yaml("target: {host: h}\nauth: {mode: authentik}\n"), NoSecrets);

        await Assert.That(check.Problems.Single()).Contains("matches none of the allowed shapes");
    }

    [Test]
    public async Task ReferencedSecret_IsRequiredOnlyInTheBranchThatUsesIt()
    {
        var manifest = Parse(Text());
        var none = ConfigValidation.Check(manifest, 9, Yaml("target: {host: h}\nauth: {mode: none}\n"), NoSecrets);
        var dex = Yaml("target: {host: h}\nauth: {mode: dex, clientId: c, clientSecret: {secret: DEX_CLIENT_SECRET}}\n");
        var dexUnset = ConfigValidation.Check(manifest, 9, dex, NoSecrets);
        var dexSet = ConfigValidation.Check(manifest, 9, dex, DexSecretSet);
        var unknown = ConfigValidation.Check(manifest, 9, dex, null);

        await Assert.That(none.Satisfied).IsTrue();
        await Assert.That(dexUnset.Reasons).IsEquivalentTo([$"secret {Secret} not set"]);
        await Assert.That(dexSet.Satisfied).IsTrue();
        await Assert.That(dexSet.ReferencedSecrets).IsEquivalentTo([Secret]);
        await Assert.That(unknown.MissingSecrets).IsEquivalentTo([Secret]);
    }

    [Test]
    public async Task ChangedDefault_TheDocumentReliesOn_IsListed()
    {
        var active = Parse(Text(namespaceDefault: "olve"));
        var target = Parse(Text(namespaceDefault: "apps"));

        var relies = ConfigValidation.Check(target, 9, Yaml("target: {host: h}\nauth: {mode: none}\n"), NoSecrets, active);
        var setsIt = ConfigValidation.Check(target, 9, Yaml("target: {host: h, namespace: mine}\nauth: {mode: none}\n"), NoSecrets, active);

        await Assert.That(relies.ChangedDefaults).IsEquivalentTo([new ChangedDefault("target.namespace", "\"olve\"", "\"apps\"")]);
        await Assert.That(relies.Satisfied).IsTrue();
        await Assert.That(setsIt.ChangedDefaults).IsEmpty();
    }

    [Test]
    public async Task ChangedDefault_IsListed_EvenWhenTheTargetRejectsTheDocument()
    {
        var active = Parse(Text(namespaceDefault: "olve"));
        var target = Parse(Text(namespaceDefault: "apps"));

        var check = ConfigValidation.Check(target, 9, Yaml("target: {host: h}\n"), NoSecrets, active: Parse(Text(namespaceDefault: "olve", authRequired: false)));

        await Assert.That(check.Valid).IsFalse();
        await Assert.That(check.ChangedDefaults.Select(d => d.Path)).IsEquivalentTo(["target.namespace"]);
        await Assert.That(active.Config.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ForStep_SlicesTheDeclaredEntries()
    {
        var manifest = Parse(Text());
        var hello = ManifestVersioning.AllSteps(manifest).Single(s => s.Name == "hello");
        var count = ManifestVersioning.AllSteps(manifest).Single(s => s.Name == "count");
        const string config = """{"auth":{"mode":"none"},"target":{"host":"h","namespace":"olve"}}""";

        await Assert.That(ConfigDelivery.WithConfig("""{"request":{}}""", hello, config))
            .IsEqualTo("""{"request":{},"$config":{"auth":{"mode":"none"},"target":{"host":"h","namespace":"olve"}}}""");
        await Assert.That(ConfigDelivery.WithConfig("""{"greeting":{}}""", count, config)).IsEqualTo("""{"greeting":{}}""");
    }

    [Test]
    public async Task OneOf_MatchingMoreThanOneBranch_IsAProblem()
    {
        using var schema = JsonDocument.Parse("""{"oneOf":[{"type":"string"},{"type":"string","minLength":1}]}""");

        await Assert.That(JsonSchemaValidator.Validate(schema.RootElement, System.Text.Json.Nodes.JsonValue.Create("x"))).Failed();
        await Assert.That(JsonSchemaValidator.Validate(schema.RootElement, System.Text.Json.Nodes.JsonValue.Create(""))).Succeeded();
    }

    [Test]
    public async Task Record_KeysAreCheckedAgainstPropertyNames()
    {
        // z.record(z.string().regex(/^[a-z.]+$/), z.string())
        using var schema = JsonDocument.Parse("""{"type":"object","propertyNames":{"type":"string","pattern":"^[a-z.]+$"},"additionalProperties":{"type":"string"}}""");

        await Assert.That(JsonSchemaValidator.Validate(schema.RootElement, System.Text.Json.Nodes.JsonNode.Parse("""{"slo.enabled":"false"}"""))).Succeeded();
        await Assert.That(JsonSchemaValidator.Validate(schema.RootElement, System.Text.Json.Nodes.JsonNode.Parse("""{"Bad Key":"x"}"""))).Failed();
    }
}
