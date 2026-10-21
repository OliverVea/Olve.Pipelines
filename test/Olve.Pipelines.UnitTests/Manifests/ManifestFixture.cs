using System.Text;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;

namespace Olve.Pipelines.UnitTests.Manifests;

/// <summary>The self-deploy manifest (real `olve-dev describe` output) plus JSON mutation helpers.</summary>
internal static class ManifestFixture
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Manifests", "Fixtures", "self-deploy.manifest.json");

    public static byte[] Bytes => File.ReadAllBytes(Path);

    public static string Text => Encoding.UTF8.GetString(Bytes);

    public static JsonObject Node() => JsonNode.Parse(Text)!.AsObject();

    public static Manifest Parse() => ParseNode(Node());

    public static Manifest ParseNode(JsonNode node)
    {
        if (!ManifestParser.TryParse(Encoding.UTF8.GetBytes(node.ToJsonString()), out var manifest, out var issues))
            throw new InvalidOperationException("fixture mutation does not parse: " + string.Join("; ", issues));
        return manifest;
    }

    public static Manifest Mutate(Action<JsonObject> mutate)
    {
        var node = Node();
        mutate(node);
        return ParseNode(node);
    }

    /// <summary>Finds a step object by name (stages, nested when blocks, failure handlers).</summary>
    public static JsonObject Step(JsonObject root, string name) =>
        FindStep(root["stages"]!.AsArray(), name)
        ?? root["failureHandlers"]!.AsArray().Select(fh => fh!["step"]!.AsObject()).FirstOrDefault(s => (string?)s["name"] == name)
        ?? throw new InvalidOperationException($"no step '{name}' in fixture");

    private static JsonObject? FindStep(JsonArray stages, string name)
    {
        foreach (var stage in stages)
        {
            if (stage!["steps"] is JsonArray steps)
            {
                var hit = steps.Select(s => s!.AsObject()).FirstOrDefault(s => (string?)s["name"] == name);
                if (hit is not null) return hit;
            }
            if (stage["stages"] is JsonArray inner && FindStep(inner, name) is { } found) return found;
        }
        return null;
    }

    public static JsonNode Ref(string r) => new JsonObject { ["ref"] = r };

    public static JsonNode Const(JsonNode? v) => new JsonObject { ["const"] = v };

    public static JsonNode StringSchema() => new JsonObject { ["schema"] = new JsonObject { ["output"] = new JsonObject { ["type"] = "string" } } };

    public static JsonNode ModelType(string model, bool optional = false) => new JsonObject { ["model"] = model, ["optional"] = optional };

    /// <summary>The same JSON with every object's keys in reverse order (for order-independence tests).</summary>
    public static JsonNode? ReverseKeys(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Reverse().Select(kv => KeyValuePair.Create(kv.Key, ReverseKeys(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(ReverseKeys).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}

/// <summary>The <c>.pipelines-test</c> fixture manifest (real <c>olve-build</c> output): hello | count, echo | when mode=normal: last.</summary>
internal static class TestFixtureManifest
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Manifests", "Fixtures", "test-fixture.manifest.json");

    public static string Text => File.ReadAllText(Path);

    public static Manifest Parse()
    {
        if (!ManifestParser.TryParse(File.ReadAllBytes(Path), out var manifest, out var issues))
            throw new InvalidOperationException("test fixture does not parse: " + string.Join("; ", issues));
        return manifest;
    }
}
