using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.UnitTests.Configs;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;

namespace Olve.Pipelines.UnitTests.Manifests;

/// <summary>Valid manifests near a rule's edge. A false error here rejects a working pipeline at reconcile and stalls its deploy; each row's rejected neighbour is in <see cref="ManifestIssueTests"/>.</summary>
public class ManifestAcceptanceTests
{
    private const string Text = """{"type":"string"}""";

    private static readonly Dictionary<string, Func<string>> Cases = new()
    {
        // external inputs
        ["person input"] = () => Externally(Source("person")),
        ["callback input with every URL-safe character"] = () => Externally(Source("callback"), key: "Go_ahead-1"),
        ["poll input at the shortest interval, over http"] = () => Externally(Poll("http://x.example/", 10)),
        ["poll input with run placeholders in the path"] = () => Externally(Poll("https://x.example/{run.id}/{run.number}", 30)),
        ["poll input with a run placeholder in the host"] = () => Externally(Poll("https://{run.id}.example/", 30)),
        ["input with one source of each kind"] = () => Externally(Source("person"), Source("callback"), Poll("https://x.example/", 30)),
        ["input with a waiting link into a regular input"] = () => Linked(new JsonObject { ["input"] = "artifact", ["path"] = "version", ["label"] = "Review" }),
        ["input with a waiting link to a whole regular input"] = () => Linked(new JsonObject { ["input"] = "artifact" }),

        // field resolution
        ["ref to a field several branches have, of unknown type"] = () => Edit(r => MapField(r, """{"oneOf":[{"properties":{"a":{"type":"string"}}},{"properties":{"a":{"type":"number"}}}]}""", ".a", """{"type":"boolean"}""")),
        ["ref into an open record"] = () => Edit(r => MapField(r, """{"type":"object","additionalProperties":true}""", ".any", Text)),
        ["ref into z.unknown"] = () => Edit(r => MapField(r, "{}", ".a.b", Text)),
        ["ref into z.any with annotations"] = () => Edit(r => MapField(r, """{"description":"d","title":"t","default":1,"$comment":"c"}""", ".a", Text)),
        ["integer into a number"] = () => Edit(r => MapField(r, """{"type":"integer"}""", "", """{"type":"number"}""")),
        ["nullable integer into a number"] = () => Edit(r => MapField(r, """{"type":["integer","null"]}""", "", """{"type":"number"}""")),
        ["extra field mapped into an open object"] = () => Edit(r => MapObject(r, """{"type":"object","properties":{"a":{"type":"string"}}}""", new JsonObject { ["a"] = Const("x"), ["extra"] = Const(1) })),
        ["extra field mapped where additionalProperties is true"] = () => Edit(r => MapObject(r, """{"type":"object","properties":{},"additionalProperties":true}""", new JsonObject { ["extra"] = Const(1) })),
        ["empty object mapped into a union of two objects"] = () => Edit(r => MapObject(r, """{"oneOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]},{"type":"object","properties":{"b":{"type":"string"}},"required":["b"]}]}""")),

        // config references
        ["config ref to a part of an entry with no secret"] = () => HelloRefersTo("auth.clientId"),
        ["narrowed ref needs only its branch's secret"] = () => SelectTests.Node(n => SelectTests.SecretPerCase(n)).ToJsonString(),
        ["narrowed ref at a nested union"] = () => SelectTests.Node(n => SelectTests.SecretPerCase(n, nested: true)).ToJsonString(),
        ["step in two nested selects keeps the outer one's ref"] = () => SelectTests.Node(n =>
        {
            var cases = n["stages"]![1]!["cases"]!.AsObject();
            var stages = cases["ssh"]!.AsArray();
            cases["ssh"] = new JsonArray(new JsonObject
            {
                ["select"] = "auth",
                ["discriminator"] = "mode",
                ["cases"] = new JsonObject { ["none"] = stages.DeepClone(), ["dex"] = stages.DeepClone() },
            });
        }).ToJsonString(),
    };

    private static JsonObject Source(string kind) => new() { ["kind"] = kind };

    private static JsonObject Poll(string url, int interval) => new() { ["kind"] = "poll", ["url"] = url, ["valuePath"] = "v", ["intervalSeconds"] = interval };

    private static string Externally(JsonObject source) => Externally([source], "decision");

    private static string Externally(JsonObject source, string key) => Externally([source], key);

    private static string Externally(params JsonObject[] sources) => Externally(sources, "decision");

    private static string Externally(JsonObject[] sources, string key) => Edit(r => Step(r, "deploy-beta")["externalInputs"] = new JsonObject
    {
        [key] = new JsonObject { ["model"] = "BuildArtifact", ["title"] = "Go?", ["from"] = new JsonArray(sources.Cast<JsonNode?>().ToArray()) },
    });

    private static string Linked(JsonObject link) => Edit(r => Step(r, "deploy-beta")["externalInputs"] = new JsonObject
    {
        ["decision"] = new JsonObject { ["model"] = "BuildArtifact", ["title"] = "Go?", ["from"] = new JsonArray(Source("person")), ["link"] = link },
    });

    private static string HelloRefersTo(string reference)
    {
        var node = JsonNode.Parse(ConfigManifests.Text(helloDeclaresSecret: false))!.AsObject();
        node["stages"]![0]!["steps"]![0]!["config"] = new JsonObject { ["x"] = new JsonObject { ["$config"] = reference } };
        return node.ToJsonString();
    }

    public static IEnumerable<string> CaseNames() => Cases.Keys;

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task AValidManifest_HasNoErrors(string name)
    {
        var report = ManifestValidator.Validate(ConfigManifests.Parse(Cases[name]()));

        await Assert.That(report.Errors.Select(e => $"{e.Path}: {e.Message}")).IsEmpty();
    }
}
