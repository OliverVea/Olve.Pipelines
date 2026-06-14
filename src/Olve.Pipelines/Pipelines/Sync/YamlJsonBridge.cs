using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Converts YAML into a <c>System.Text.Json</c> <see cref="JsonNode"/> DOM using YamlDotNet's
/// reflection-free <see cref="YamlStream"/> parser. This keeps the whole config pipeline
/// AOT-safe (no reflection-based YAML object mapper) and lets the manifest deserialize through
/// the existing source-generated <c>System.Text.Json</c> metadata, polymorphic trigger targets
/// included. All scalars are emitted as JSON strings; number fields are read back via
/// <c>JsonNumberHandling.AllowReadingFromString</c>.
/// </summary>
internal static class YamlJsonBridge
{
    /// <summary>Parses the first document of <paramref name="yaml"/>. Returns null for empty input.</summary>
    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => ConvertMap(map),
        YamlSequenceNode seq => ConvertSequence(seq),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null,
    };

    private static JsonObject ConvertMap(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            if (key is YamlScalarNode { Value: { } name })
                obj[name] = Convert(value);
        }

        return obj;
    }

    private static JsonArray ConvertSequence(YamlSequenceNode seq)
    {
        var arr = new JsonArray();
        foreach (var item in seq.Children)
            arr.Add(Convert(item));

        return arr;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
            return null;

        // Only an unquoted (plain) null/~/empty is JSON null; a quoted "null" stays a string.
        if (scalar.Style == ScalarStyle.Plain && value is "null" or "~" or "")
            return null;

        return JsonValue.Create(value);
    }
}
