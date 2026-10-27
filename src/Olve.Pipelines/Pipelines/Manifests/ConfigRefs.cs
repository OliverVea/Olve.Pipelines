using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// Config references in a library step's configuration: <c>{"$config": "target.beta.host"}</c> (entry name, then
/// the field path). The harness resolves them from the step's delivered <c>$config</c> when the step runs.
/// </summary>
public static class ConfigRefs
{
    public const string Key = "$config";
    /// <summary>Beside <see cref="Key"/> on a reference narrowed by a select case: the case's condition (<c>target.prod.kind=ssh</c>).</summary>
    public const string CaseKey = "$case";

    /// <summary>Every reference inside <paramref name="value"/>, with its JSON path and the select case it is narrowed to.</summary>
    public static IEnumerable<(string Path, string Reference, string? Case)> Find(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGet(value, out var reference, out var condition))
                {
                    yield return (path, reference, condition);
                    yield break;
                }
                foreach (var p in value.EnumerateObject())
                    foreach (var found in Find(p.Value, $"{path}.{p.Name}"))
                        yield return found;
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    foreach (var found in Find(item, $"{path}[{i++}]"))
                        yield return found;
                break;
        }
    }

    /// <summary>
    /// The schemas at <paramref name="path"/> inside an entry's schema: every union branch that has the field, except
    /// where <paramref name="condition"/> (<c>&lt;entry&gt;.&lt;path&gt;.&lt;discriminator&gt;=&lt;value&gt;</c>) narrows a union to one branch.
    /// </summary>
    public static IEnumerable<JsonElement> SubSchemas(JsonElement schema, IReadOnlyList<string> path, string? condition, string entry)
    {
        (string At, string Discriminator, string Value)? narrow = null;
        if (condition is not null && condition.LastIndexOf('=') is var eq and > 0)
        {
            var left = condition[..eq];
            var dot = left.LastIndexOf('.');
            if (dot > 0) narrow = (left[..dot], left[(dot + 1)..], condition[(eq + 1)..]);
        }
        return Walk(schema, 0, entry);

        IEnumerable<JsonElement> Walk(JsonElement node, int at, string here)
        {
            if (node.ValueKind != JsonValueKind.Object) yield break;
            if ((node.TryGetProperty("oneOf", out var branches) || node.TryGetProperty("anyOf", out branches)) && branches.ValueKind == JsonValueKind.Array)
            {
                foreach (var branch in branches.EnumerateArray())
                {
                    if (narrow is { } n && n.At == here && !(branch.TryGetProperty("properties", out var bp) && bp.TryGetProperty(n.Discriminator, out var d)
                            && d.TryGetProperty("const", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == n.Value))
                        continue;
                    foreach (var found in Walk(branch, at, here)) yield return found;
                }
                yield break;
            }
            if (at == path.Count)
            {
                yield return node;
                yield break;
            }
            if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object && props.TryGetProperty(path[at], out var child))
                foreach (var found in Walk(child, at + 1, $"{here}.{path[at]}")) yield return found;
        }
    }

    /// <summary>True when <paramref name="value"/> is <c>{"$config": "&lt;reference&gt;"}</c>, optionally with <c>"$case"</c>.</summary>
    public static bool TryGet(JsonElement value, out string reference, out string? condition)
    {
        reference = "";
        condition = null;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        foreach (var p in value.EnumerateObject())
        {
            count++;
            if (p.Value.ValueKind != JsonValueKind.String) return false;
            if (p.Name == Key) reference = p.Value.GetString()!;
            else if (p.Name == CaseKey) condition = p.Value.GetString();
            else return false;
        }
        return reference.Length > 0 && count == (condition is null ? 1 : 2);
    }
}
