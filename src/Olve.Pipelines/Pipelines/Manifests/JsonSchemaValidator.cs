using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// A small, AOT-safe JSON Schema validator for the subset zod's <c>toJSONSchema</c> emits for pipeline
/// input models: <c>type</c> (incl. <c>integer</c> and type arrays), <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c> (bool or schema), <c>enum</c>, <c>const</c>, <c>default</c> (applied to
/// absent properties), <c>items</c>, <c>anyOf</c> (incl. nullable), string <c>pattern</c>/<c>minLength</c>/
/// <c>maxLength</c>, number <c>minimum</c>/<c>maximum</c>/<c>exclusiveMinimum</c>/<c>exclusiveMaximum</c>.
/// Annotations (<c>title</c>, <c>description</c>, <c>format</c>, <c>x-olve-kind</c>, …) are ignored. Any
/// other keyword (<c>$ref</c>, <c>allOf</c>, <c>oneOf</c>, <c>not</c>, …) is rejected explicitly rather than
/// silently skipped. Schemas come from untrusted manifests: patterns run with a match timeout.
/// </summary>
public static class JsonSchemaValidator
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "enum", "const", "default", "items", "anyOf",
        "pattern", "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
    };

    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$comment", "title", "description", "examples", "format", "deprecated", "readOnly", "writeOnly",
        "x-olve-kind", "id", "$id",
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private const int MaxDepth = 64;

    /// <summary>
    /// Validates <paramref name="value"/> against <paramref name="schema"/>, filling defaults for absent
    /// object properties. Returns the value with defaults applied, or one problem per violation
    /// (<c>input.mode: must be one of …</c>).
    /// </summary>
    public static Result<JsonNode?> Validate(JsonElement schema, JsonNode? value, string root = "input")
    {
        var errors = new List<string>();
        var result = Check(schema, value?.DeepClone(), root, errors, 0);
        return errors.Count == 0
            ? Result.Success(result)
            : Result.Failure<JsonNode?>(errors.Select(e => new ResultProblem("{0}", e)));
    }

    private static JsonNode? Check(JsonElement schema, JsonNode? value, string path, List<string> errors, int depth)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: schema nesting is too deep");
            return value;
        }

        if (schema.ValueKind == JsonValueKind.True) return value;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{path}: no value is allowed here");
            return value;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: invalid schema (not an object)");
            return value;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!Supported.Contains(keyword.Name) && !Annotations.Contains(keyword.Name))
            {
                errors.Add($"{path}: the input schema uses '{keyword.Name}', which this service does not support");
                return value;
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
            return CheckAnyOf(schema, anyOf, value, path, errors, depth);

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            errors.Add($"{path}: expected {Describe(type)}, got {KindOf(value)}");
            return value;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonNode.DeepEquals(ToNode(constant), value))
            errors.Add($"{path}: must be {ToNode(constant)?.ToJsonString() ?? "null"}");

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array
            && !enumValues.EnumerateArray().Any(e => JsonNode.DeepEquals(ToNode(e), value)))
            errors.Add($"{path}: must be one of {ToNode(enumValues)?.ToJsonString()}");

        switch (value)
        {
            case JsonObject obj:
                CheckObject(schema, obj, path, errors, depth);
                break;
            case JsonArray array when schema.TryGetProperty("items", out var items):
                for (var i = 0; i < array.Count; i++)
                {
                    var checkedItem = Check(items, array[i], $"{path}[{i}]", errors, depth + 1);
                    if (!ReferenceEquals(checkedItem, array[i]))
                        array[i] = checkedItem;
                }
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                CheckString(schema, v.GetValue<string>(), path, errors);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                CheckNumber(schema, v, path, errors);
                break;
        }

        return value;
    }

    private static JsonNode? CheckAnyOf(JsonElement schema, JsonElement anyOf, JsonNode? value, string path, List<string> errors, int depth)
    {
        if (anyOf.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}: invalid schema ('anyOf' is not an array)");
            return value;
        }

        var branchErrors = new List<string>();
        foreach (var branch in anyOf.EnumerateArray())
        {
            var attempt = new List<string>();
            var result = Check(branch, value?.DeepClone(), path, attempt, depth + 1);
            if (attempt.Count == 0)
            {
                // Sibling keywords of anyOf (rare in zod output) still apply.
                var siblings = new List<string>();
                foreach (var keyword in schema.EnumerateObject())
                {
                    if (keyword.Name is "anyOf" || Annotations.Contains(keyword.Name) || keyword.Name == "default") continue;
                    siblings.Add(keyword.Name);
                }
                if (siblings.Count > 0)
                    errors.Add($"{path}: 'anyOf' combined with {string.Join(", ", siblings)} is not supported");
                return result;
            }
            branchErrors.AddRange(attempt);
        }

        errors.Add($"{path}: matches none of the allowed shapes ({string.Join("; ", branchErrors.Distinct())})");
        return value;
    }

    private static void CheckObject(JsonElement schema, JsonObject obj, string path, List<string> errors, int depth)
    {
        var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props
            : (JsonElement?)null;

        // Defaults first, so required/when see the filled-in value (the z.input → z.output step).
        if (properties is { } ps)
        {
            foreach (var p in ps.EnumerateObject())
            {
                if (!obj.ContainsKey(p.Name) && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("default", out var def))
                    obj[p.Name] = ToNode(def);
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in required.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String && !obj.ContainsKey(r.GetString()!))
                    errors.Add($"{path}.{r.GetString()}: is required");
            }
        }

        var additional = schema.TryGetProperty("additionalProperties", out var ap) ? ap : (JsonElement?)null;
        foreach (var name in obj.Select(kv => kv.Key).ToList())
        {
            var childPath = $"{path}.{name}";
            if (properties is { } known && known.TryGetProperty(name, out var propertySchema))
            {
                var child = obj[name];
                var checkedChild = Check(propertySchema, child, childPath, errors, depth + 1);
                if (!ReferenceEquals(checkedChild, child))
                    obj[name] = checkedChild;
            }
            else if (additional is { ValueKind: JsonValueKind.False })
            {
                errors.Add($"{childPath}: is not allowed (unknown property)");
            }
            else if (additional is { ValueKind: JsonValueKind.Object } additionalSchema)
            {
                Check(additionalSchema, obj[name], childPath, errors, depth + 1);
            }
        }
    }

    private static void CheckString(JsonElement schema, string s, string path, List<string> errors)
    {
        var length = new StringInfo(s).LengthInTextElements;
        if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt64(out var minLength) && length < minLength)
            errors.Add($"{path}: must be at least {minLength} characters");
        if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt64(out var maxLength) && length > maxLength)
            errors.Add($"{path}: must be at most {maxLength} characters");
        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(s, pattern.GetString()!, RegexOptions.None, RegexTimeout))
                    errors.Add($"{path}: must match {pattern.GetString()}");
            }
            catch (ArgumentException)
            {
                errors.Add($"{path}: the schema's pattern is not a supported regular expression");
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add($"{path}: the schema's pattern took too long to evaluate");
            }
        }
    }

    private static void CheckNumber(JsonElement schema, JsonValue v, string path, List<string> errors)
    {
        var n = ToDouble(v);
        if (schema.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.Number && n < min.GetDouble())
            errors.Add($"{path}: must be ≥ {min.GetRawText()}");
        if (schema.TryGetProperty("maximum", out var max) && max.ValueKind == JsonValueKind.Number && n > max.GetDouble())
            errors.Add($"{path}: must be ≤ {max.GetRawText()}");
        if (schema.TryGetProperty("exclusiveMinimum", out var xmin) && xmin.ValueKind == JsonValueKind.Number && n <= xmin.GetDouble())
            errors.Add($"{path}: must be > {xmin.GetRawText()}");
        if (schema.TryGetProperty("exclusiveMaximum", out var xmax) && xmax.ValueKind == JsonValueKind.Number && n >= xmax.GetDouble())
            errors.Add($"{path}: must be < {xmax.GetRawText()}");
    }

    private static bool MatchesType(JsonElement type, JsonNode? value) => type.ValueKind switch
    {
        JsonValueKind.String => MatchesType(type.GetString()!, value),
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && MatchesType(t.GetString()!, value)),
        _ => false,
    };

    private static bool MatchesType(string type, JsonNode? value) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue s && s.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && IsInteger(i),
        _ => false,
    };

    private static bool IsInteger(JsonValue v)
    {
        var d = ToDouble(v);
        return Math.Abs(d % 1) < double.Epsilon && !double.IsInfinity(d);
    }

    private static double ToDouble(JsonValue v)
        => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Describe(JsonElement type) => type.ValueKind == JsonValueKind.Array
        ? string.Join(" or ", type.EnumerateArray().Select(t => t.GetRawText().Trim('"')))
        : type.GetRawText().Trim('"');

    private static string KindOf(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "value",
        },
        _ => "value",
    };

    private static JsonNode? ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText());
}
