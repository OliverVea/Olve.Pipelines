using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// Secret references in config: a <c>secretRef(S)</c> schema is <c>{"x-olve-kind": "secretRef", properties: {secret: {const: "S"}}}</c>,
/// and its value in a config document is <c>{secret: "S"}</c>.
/// </summary>
public static class ConfigSecretRefs
{
    public const string Kind = "secretRef";

    /// <summary>Every secret a schema can reference (any branch).</summary>
    public static IReadOnlySet<string> Declared(JsonElement schema)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Walk(schema, names, 0);
        return names;
    }

    /// <summary>The secrets a (validated) value actually references: <c>{secret: S}</c> objects naming one of <paramref name="declared"/>.</summary>
    public static IReadOnlySet<string> Used(JsonNode? value, IReadOnlySet<string> declared)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Collect(value, declared, names, 0);
        return names;
    }

    private static void Walk(JsonElement schema, SortedSet<string> names, int depth)
    {
        if (depth > 64) return;
        switch (schema.ValueKind)
        {
            case JsonValueKind.Object:
                if (schema.TryGetProperty("x-olve-kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == Kind
                    && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                    && props.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.Object
                    && secret.TryGetProperty("const", out var name) && name.ValueKind == JsonValueKind.String)
                    names.Add(name.GetString()!);
                foreach (var p in schema.EnumerateObject()) Walk(p.Value, names, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in schema.EnumerateArray()) Walk(item, names, depth + 1);
                break;
        }
    }

    private static void Collect(JsonNode? value, IReadOnlySet<string> declared, SortedSet<string> names, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case JsonObject o when o.Count == 1 && o.TryGetPropertyValue("secret", out var s) && s is JsonValue v
                                   && v.TryGetValue<string>(out var name) && declared.Contains(name):
                names.Add(name);
                break;
            case JsonObject o:
                foreach (var (_, child) in o) Collect(child, declared, names, depth + 1);
                break;
            case JsonArray a:
                foreach (var child in a) Collect(child, declared, names, depth + 1);
                break;
        }
    }
}
