// Generates docs/setup/api-reference.md from api.json and tools/api-reference.header.md.
//
//   dotnet run tools/api-reference.cs              # write it
//   dotnet run tools/api-reference.cs -- --check   # exit 1 when the committed page is out of date
using System.Text;
using System.Text.Json.Nodes;

var root = Directory.GetCurrentDirectory();
var check = args.Contains("--check");
var outPath = Path.Combine(root, "docs", "setup", "api-reference.md");

var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "api.json")))!;
var schemas = doc["components"]?["schemas"]?.AsObject() ?? new JsonObject();
string[] tagOrder = ["Pipelines", "Bindings", "Versions", "Config", "Secrets", "Runs", "Steps", "Attempts", "Triggers", "Events", "Instance"];

var md = new StringBuilder(File.ReadAllText(Path.Combine(root, "tools", "api-reference.header.md")).TrimEnd()).Append("\n\n");

var ops = new List<(string Tag, string Method, string Path, JsonObject Op)>();
foreach (var (path, item) in doc["paths"]!.AsObject())
    foreach (var (method, op) in item!.AsObject())
        if (op is JsonObject o)
            ops.Add((o["tags"]?[0]?.GetValue<string>() ?? "Other", method.ToUpperInvariant(), path, o));
var tags = tagOrder.Concat(ops.Select(o => o.Tag).Distinct().Except(tagOrder).Order(StringComparer.Ordinal)).Where(t => ops.Any(o => o.Tag == t));

md.Append("## Endpoints\n\n");
foreach (var tag in tags)
{
    md.Append($"### {tag}\n\n");
    foreach (var (_, method, path, op) in ops.Where(o => o.Tag == tag))
    {
        md.Append($"#### {Str(op["summary"]) ?? Str(op["operationId"]) ?? $"{method} {path}"}\n\n");
        md.Append($"`{method} {path}`\n\n");
        if (Str(op["description"]) is { } description) md.Append(description).Append("\n\n");

        if (op["parameters"] is JsonArray { Count: > 0 } parameters)
        {
            md.Append("| Parameter | In | Type | Required |\n|---|---|---|---|\n");
            foreach (var p in parameters)
                md.Append($"| `{Str(p!["name"])}` | {Str(p["in"])} | {Type(p["schema"])} | {(p["required"]?.GetValue<bool>() == true ? "yes" : "")} |\n");
            md.Append('\n');
        }
        if (op["requestBody"]?["content"] is JsonObject body)
            md.Append($"**Body** ({string.Join(", ", body.Select(c => $"`{c.Key}`"))}): {Type(body.First().Value?["schema"])}\n\n");

        var success = (op["responses"]?.AsObject() ?? new JsonObject()).Where(r => r.Key.StartsWith('2')).ToList();
        if (success.Count > 0)
        {
            md.Append("**Returns:**");
            foreach (var (code, response) in success)
            {
                var content = response?["content"]?.AsObject().FirstOrDefault();
                md.Append($" `{code}`{(content?.Value?["schema"] is { } s ? " " + Type(s) : "")}{(content is { Key: { } type } && type != "application/json" ? $" (`{type}`)" : "")}");
            }
            md.Append("\n\n");
        }
    }
}

md.Append("## Schemas\n\n");
foreach (var (name, schema) in schemas.OrderBy(s => s.Key, StringComparer.Ordinal))
{
    if (!IsDocumented(schema!)) continue;
    md.Append($"### {name}\n\n");
    if (Str(schema!["description"]) is { } description) md.Append(description).Append("\n\n");
    var properties = Properties(schema).ToList();
    if (properties.Count > 0)
    {
        md.Append("| Field | Type | |\n|---|---|---|\n");
        foreach (var (field, type, required) in properties)
            md.Append($"| `{field}` | {type} | {(required ? "required" : "")} |\n");
        md.Append('\n');
    }
    else
        md.Append($"{Type(schema)}\n\n");
}

var text = md.ToString().TrimEnd() + "\n";
if (check)
{
    if (!File.Exists(outPath) || File.ReadAllText(outPath) != text)
    {
        Console.Error.WriteLine("docs/setup/api-reference.md is out of date with api.json: run `dotnet run tools/api-reference.cs` and commit it");
        return 1;
    }
    Console.WriteLine("docs/setup/api-reference.md matches api.json");
    return 0;
}
File.WriteAllText(outPath, text);
Console.WriteLine($"wrote {Path.GetRelativePath(root, outPath)}");
return 0;

static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

// Objects (and unions of them) get their own section; primitives, enums and ids are written inline.
bool IsDocumented(JsonNode schema) => schema["properties"] is not null || schema["allOf"] is not null || schema["oneOf"] is not null || schema["anyOf"] is not null;

IEnumerable<(string Field, string Type, bool Required)> Properties(JsonNode schema)
{
    foreach (var part in schema["allOf"]?.AsArray() ?? [])
    {
        if (Str(part?["$ref"]) is null)
            foreach (var p in Properties(part!)) yield return p;
    }
    var required = (schema["required"]?.AsArray() ?? []).Select(Str).ToHashSet();
    foreach (var (field, prop) in schema["properties"]?.AsObject() ?? new JsonObject())
        yield return (field, Type(prop), required.Contains(field));
}

string Type(JsonNode? s)
{
    if (s is null) return "any";
    if (Str(s["$ref"]) is { } reference)
    {
        var name = reference[(reference.LastIndexOf('/') + 1)..];
        return schemas[name] is { } target && !IsDocumented(target) ? Type(target) : $"[{name}](#{name.ToLowerInvariant()})";
    }
    if (s["enum"] is JsonArray values) return string.Join(" \\| ", values.Select(v => $"`{v?.ToJsonString()}`"));
    if (s["oneOf"] is JsonArray oneOf || s["anyOf"] is JsonArray anyOf && (oneOf = anyOf) is not null)
        return Union(oneOf.Select(Type));
    if (s["allOf"] is JsonArray allOf)
    {
        var refs = allOf.Where(p => Str(p?["$ref"]) is not null).Select(Type).ToList();
        return refs.Count > 0 && s["properties"] is null && allOf.Count == refs.Count ? string.Join(" & ", refs) : "object";
    }
    var types = s["type"] switch
    {
        JsonArray a => a.Select(Str).OfType<string>().ToList(),
        JsonValue v => [v.GetValue<string>()],
        _ => [],
    };
    var nullable = types.Remove("null");
    if (nullable && types.Count == 0) return "null";
    if (types.Contains("integer")) types = ["integer"];
    var core = types.Count switch
    {
        0 => s["properties"] is not null || s["additionalProperties"] is not null ? "object" : "any",
        _ => types[0],
    };
    if (core == "array") core = $"{Type(s["items"])}[]";
    else if (core == "object" && s["additionalProperties"] is JsonObject values2) core = $"map of {Type(values2)}";
    else if (Str(s["format"]) is { } format && core == "string") core = $"string ({format})";
    return nullable ? Union([core, "null"]) : core;
}

static string Union(IEnumerable<string> parts)
{
    var list = parts.Distinct().ToList();
    var nullable = list.Remove("null");
    var core = string.Join(" or ", list);
    return nullable ? $"{core}?" : core;
}
