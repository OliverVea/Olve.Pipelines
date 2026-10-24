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
