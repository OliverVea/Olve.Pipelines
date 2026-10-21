using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>
/// Assembles the local pipeline input from <c>--input k=v</c> pairs, typed by the input model's
/// <c>z.input</c> schema, layered over a base (the pool's previous input), with the schema's
/// <c>default</c>s filled in — the same shape the service stores as a run's input.
/// </summary>
public static class PipelineInput
{
    /// <summary>
    /// Builds the input: <paramref name="baseInput"/> (may be null), then each <c>k=v</c> in
    /// <paramref name="pairs"/>, then defaults for still-missing properties.
    /// </summary>
    public static Result<JsonObject> Build(JsonObject? schema, JsonObject? baseInput, IReadOnlyList<string> pairs)
    {
        var input = baseInput is null ? new JsonObject() : (JsonObject)baseInput.DeepClone();
        var properties = schema?["properties"] as JsonObject;
        var problems = new List<ResultProblem>();

        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                problems.Add(new ResultProblem("--input expects key=value, got '{0}'.", pair));
                continue;
            }

            var key = pair[..eq];
            var raw = pair[(eq + 1)..];
            if (properties is not null && !properties.ContainsKey(key))
            {
                problems.Add(new ResultProblem(
                    "Unknown pipeline input field '{0}' (known: {1}).", key, string.Join(", ", properties.Select(p => p.Key))));
                continue;
            }

            if (Coerce(key, raw, properties?[key] as JsonObject).TryPickProblems(out var coerceProblems, out var value))
            {
                problems.AddRange(coerceProblems);
                continue;
            }

            input[key] = value;
        }

        if (problems.Count > 0)
            return new ResultProblemCollection(problems);

        if (properties is not null)
        {
            foreach (var (key, property) in properties)
            {
                if (!input.ContainsKey(key) && property is JsonObject p && p.TryGetPropertyValue("default", out var def))
                    input[key] = def?.DeepClone();
            }
        }

        return input;
    }

    /// <summary>Required fields of <paramref name="schema"/> that <paramref name="input"/> lacks.</summary>
    public static IReadOnlyList<string> MissingRequired(JsonObject? schema, JsonObject input) =>
        (schema?["required"] as JsonArray ?? [])
            .Select(DevManifest.Str)
            .OfType<string>()
            .Where(field => !input.ContainsKey(field))
            .ToList();

    /// <summary>Turns the raw string of <c>key=raw</c> into JSON using the property's schema type.</summary>
    public static Result<JsonNode?> Coerce(string key, string raw, JsonObject? property)
    {
        var type = DevManifest.Str(property?["type"]);
        switch (type)
        {
            case "string":
                return JsonValue.Create(raw);
            case "boolean":
                return raw switch
                {
                    "true" => JsonValue.Create(true),
                    "false" => JsonValue.Create(false),
                    _ => new ResultProblem("Pipeline input '{0}' is a boolean: use {0}=true or {0}=false (got '{1}').", key, raw),
                };
            case "integer":
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? JsonValue.Create(l)
                    : new ResultProblem("Pipeline input '{0}' is an integer (got '{1}').", key, raw);
            case "number":
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? JsonValue.Create(d)
                    : new ResultProblem("Pipeline input '{0}' is a number (got '{1}').", key, raw);
        }

        // An untyped enum: pick the enum member whose JSON (or string form) matches.
        if (type is null && property?["enum"] is JsonArray members)
        {
            foreach (var member in members)
            {
                if (DevManifest.Str(member) == raw || member?.ToJsonString() == raw)
                    return member?.DeepClone();
            }
        }

        // Objects, arrays, unions and unknown fields: JSON if it parses, else the raw string.
        if (type is "object" or "array")
        {
            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException ex)
            {
                return new ResultProblem("Pipeline input '{0}' is a JSON {1}: {2}", key, type, ex.Message);
            }
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }
}
