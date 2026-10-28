using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Olve.Pipelines.Pipelines.Configs;

/// <summary>
/// Reads a config document (YAML) into JSON with the YAML 1.2 core schema: plain <c>true</c>/<c>false</c>,
/// <c>null</c>/<c>~</c>/empty, integers and floats are typed, everything else (and anything quoted) is a
/// string, so <c>no</c> stays a string. One document; the top level is a mapping (or empty). Anchors,
/// aliases and tags other than the core ones are rejected. Uses only YamlDotNet's event parser (AOT-safe).
/// </summary>
public static partial class YamlJson
{
    public const int MaxBytes = 256 * 1024;
    private const int MaxDepth = 64;

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntRegex();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctalRegex();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    /// <summary>Parses <paramref name="yaml"/>; problems carry the field <c>yaml</c> and a line number.</summary>
    public static Result<JsonObject> Parse(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes)
            return Problem(null, $"the document is larger than {MaxBytes / 1024} KB");

        try
        {
            var parser = new Parser(new StringReader(yaml));
            parser.Consume<StreamStart>();
            if (parser.TryConsume<StreamEnd>(out _))
                return new JsonObject();

            parser.Consume<DocumentStart>();
            JsonNode? root;
            if (parser.Accept<DocumentEnd>(out _))
                root = null;
            else if (ReadNode(parser, 0).TryPickProblems(out var problems, out root))
                return Result.Failure<JsonObject>(problems);
            parser.Consume<DocumentEnd>();
            if (!parser.TryConsume<StreamEnd>(out _))
                return Problem(parser.Current?.Start, "only one YAML document is allowed");

            return root switch
            {
                null => new JsonObject(),
                JsonObject o => o,
                _ => Problem(null, "the document must be a mapping of config entries (name: value)"),
            };
        }
        catch (YamlException e)
        {
            return Problem(e.Start, e.InnerException?.Message ?? e.Message);
        }
    }

    private static Result<JsonNode?> ReadNode(IParser parser, int depth)
    {
        if (depth > MaxDepth)
            return Problem(parser.Current?.Start, "the document is nested too deeply");

        var current = parser.Current;
        switch (current)
        {
            case AnchorAlias alias:
                return Problem(alias.Start, "anchors and aliases are not supported");
            case NodeEvent { Anchor.IsEmpty: false } anchored:
                return Problem(anchored.Start, "anchors and aliases are not supported");
            case Scalar scalar:
                parser.MoveNext();
                return ScalarValue(scalar);
            case SequenceStart sequenceStart:
                {
                    if (!sequenceStart.Tag.IsEmpty && sequenceStart.Tag.Value != "tag:yaml.org,2002:seq")
                        return Problem(sequenceStart.Start, $"tag '{sequenceStart.Tag.Value}' is not supported");
                    parser.MoveNext();
                    var array = new JsonArray();
                    while (!parser.TryConsume<SequenceEnd>(out _))
                    {
                        if (ReadNode(parser, depth + 1).TryPickProblems(out var problems, out var item))
                            return Result.Failure<JsonNode?>(problems);
                        array.Add(item);
                    }
                    return array;
                }
            case MappingStart mappingStart:
                {
                    if (!mappingStart.Tag.IsEmpty && mappingStart.Tag.Value != "tag:yaml.org,2002:map")
                        return Problem(mappingStart.Start, $"tag '{mappingStart.Tag.Value}' is not supported");
                    parser.MoveNext();
                    var obj = new JsonObject();
                    while (!parser.TryConsume<MappingEnd>(out _))
                    {
                        if (parser.Current is not Scalar key)
                            return Problem(parser.Current?.Start, "mapping keys must be plain strings");
                        parser.MoveNext();
                        if (obj.ContainsKey(key.Value))
                            return Problem(key.Start, $"duplicate key '{key.Value}'");
                        if (ReadNode(parser, depth + 1).TryPickProblems(out var problems, out var value))
                            return Result.Failure<JsonNode?>(problems);
                        obj[key.Value] = value;
                    }
                    return obj;
                }
            default:
                return Problem(current?.Start, "unexpected YAML content");
        }
    }

    private static Result<JsonNode?> ScalarValue(Scalar s)
    {
        var tag = s.Tag.IsEmpty ? null : s.Tag.Value;
        if (tag is "tag:yaml.org,2002:str" || (tag is null && s.Style is not ScalarStyle.Plain))
            return JsonValue.Create(s.Value);
        if (tag is not null && tag is not ("tag:yaml.org,2002:null" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float"))
            return Problem(s.Start, $"tag '{tag}' is not supported");

        var v = s.Value;
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return Result.Success<JsonNode?>(null);
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "-.inf" or ".nan" or ".NaN" or ".NAN":
                return Problem(s.Start, $"'{v}' cannot be represented in JSON");
        }
        if (IntRegex().IsMatch(v) && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
            return JsonValue.Create(i);
        if (OctalRegex().IsMatch(v))
            return JsonValue.Create(Convert.ToInt64(v[2..], 8));
        if (HexRegex().IsMatch(v) && long.TryParse(v[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h))
            return JsonValue.Create(h);
        if (FloatRegex().IsMatch(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return tag is null ? JsonValue.Create(v) : Problem(s.Start, $"'{v}' is not a valid {tag[(tag.LastIndexOf(':') + 1)..]}");
    }

    private static ValidationProblem Problem(Mark? at, string message)
        => at is { } mark && mark.Line > 0
            ? ValidationProblem.ForField("yaml", "line {0}: {1}", mark.Line, message)
            : ValidationProblem.ForField("yaml", "{0}", message);
}
