using Olve.Pipelines.Pipelines.Configs;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Configs;

/// <summary>The test fixture manifest with typed config: <c>target</c> (required) and <c>auth</c> (none | dex with a secretRef).</summary>
internal static class ConfigManifests
{
    public const string Secret = "DEX_CLIENT_SECRET";

    public const string TargetSchema = """
        {"type":"object","properties":{"host":{"type":"string"},"namespace":{"default":"olve","type":"string"}},"required":["host"]}
        """;

    public const string AuthSchema = """
        {"oneOf":[
          {"type":"object","properties":{"mode":{"type":"string","const":"none"}},"required":["mode"]},
          {"type":"object","properties":{"mode":{"type":"string","const":"dex"},"clientId":{"type":"string"},
            "clientSecret":{"type":"object","properties":{"secret":{"type":"string","const":"DEX_CLIENT_SECRET"}},"required":["secret"],"additionalProperties":false,"x-olve-kind":"secretRef"}},
           "required":["mode","clientId","clientSecret"]}
        ]}
        """;

    /// <summary>
    /// The fixture with <c>target</c> and <c>auth</c> declared, delivered to step <c>hello</c> (which declares the
    /// secret). <paramref name="namespaceDefault"/> changes the default of <c>target.namespace</c>.
    /// </summary>
    public static string Text(string namespaceDefault = "olve", bool helloDeclaresSecret = true, bool authRequired = true)
    {
        var node = JsonNode.Parse(TestFixtureManifest.Text)!.AsObject();
        node["secrets"] = new JsonArray(new JsonObject { ["name"] = Secret, ["description"] = "Dex client secret." });
        var target = JsonNode.Parse(TargetSchema)!;
        target["properties"]!["namespace"]!["default"] = namespaceDefault;
        node["config"] = new JsonArray(
            new JsonObject { ["name"] = "auth", ["description"] = "How users log in.", ["required"] = authRequired, ["schema"] = JsonNode.Parse(AuthSchema) },
            new JsonObject { ["name"] = "target", ["description"] = "Where to deploy.", ["required"] = true, ["schema"] = target });
        var hello = node["stages"]![0]!["steps"]![0]!.AsObject();
        hello["configEntries"] = new JsonArray("auth", "target");
        if (helloDeclaresSecret)
            hello["secrets"] = new JsonArray(Secret);
        return node.ToJsonString();
    }

    public static Manifest Parse(string text)
    {
        if (!ManifestParser.TryParse(System.Text.Encoding.UTF8.GetBytes(text), out var manifest, out var issues))
            throw new InvalidOperationException("config manifest does not parse: " + string.Join("; ", issues));
        return manifest;
    }

    public static JsonObject Yaml(string yaml) => TestRunDomain.Pick(YamlJson.Parse(yaml));

    /// <summary>A failed result's problems as display text (empty on success).</summary>
    public static string Problems<T>(Result<T> result)
        => result.TryPickProblems(out var problems, out _) ? string.Join("; ", problems.Select(p => p.ToDisplayString())) : "";

    public static string Problems(Result result)
        => result.TryPickProblems(out var problems) ? string.Join("; ", problems.Select(p => p.ToDisplayString())) : "";
}
