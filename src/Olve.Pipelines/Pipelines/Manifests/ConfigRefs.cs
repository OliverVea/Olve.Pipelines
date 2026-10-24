using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// Config references in a library step's configuration: <c>{"$config": "target.beta.host"}</c> (entry name, then
/// the field path). The harness resolves them from the step's delivered <c>$config</c> when the step runs.
/// </summary>
public static class ConfigRefs
{
    public const string Key = "$config";

    /// <summary>Every reference inside <paramref name="value"/>, with its JSON path.</summary>
    public static IEnumerable<(string Path, string Reference)> Find(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGet(value, out var reference))
                {
                    yield return (path, reference);
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

    /// <summary>True when <paramref name="value"/> is exactly <c>{"$config": "&lt;reference&gt;"}</c>.</summary>
    public static bool TryGet(JsonElement value, out string reference)
    {
        reference = "";
        if (value.ValueKind != JsonValueKind.Object) return false;
        using var props = value.EnumerateObject();
        if (!props.MoveNext() || props.Current.Name != Key || props.Current.Value.ValueKind != JsonValueKind.String) return false;
        var r = props.Current.Value.GetString()!;
        if (props.MoveNext() || r.Length == 0) return false;
        reference = r;
        return true;
    }
}
