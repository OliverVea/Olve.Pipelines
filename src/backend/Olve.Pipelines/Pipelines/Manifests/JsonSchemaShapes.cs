using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// The small slice of JSON Schema the validator needs: walking properties (through anyOf/oneOf/allOf,
/// local $refs and additionalProperties), literal values of a field, and a lenient primitive-type view.
/// </summary>
internal static class JsonSchemaShapes
{
    private const int MaxRefHops = 32;

    /// <summary>
    /// Follows <paramref name="segments"/> through <paramref name="schema"/>. Returns false if the field
    /// cannot exist. <paramref name="leaf"/> is the field's schema, or null when unknown (any branch / any-schema).
    /// </summary>
    public static bool TryResolveField(JsonElement root, JsonElement schema, IReadOnlyList<string> segments, out JsonElement? leaf) =>
        TryResolve(root, schema, segments, 0, 0, out leaf);

    private static bool TryResolve(JsonElement root, JsonElement schema, IReadOnlyList<string> segs, int at, int hops, out JsonElement? leaf)
    {
        leaf = null;
        if (schema.ValueKind == JsonValueKind.True) return true; // `true` schema accepts anything
        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (!Deref(root, ref schema, hops, out var nextHops)) return false;
        if (at == segs.Count)
        {
            leaf = schema;
            return true;
        }
        foreach (var combinator in (string[])["anyOf", "oneOf", "allOf"])
        {
            if (!schema.TryGetProperty(combinator, out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
            var found = false;
            JsonElement? only = null;
            var count = 0;
            foreach (var b in branches.EnumerateArray())
            {
                if (!TryResolve(root, b, segs, at, nextHops, out var l)) continue;
                found = true;
                count++;
                only = l;
            }
            if (found)
            {
                leaf = count == 1 ? only : null; // several matching branches → type unknown (lenient)
                return true;
            }
            return false;
        }
        var seg = segs[at];
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object && props.TryGetProperty(seg, out var prop))
            return TryResolve(root, prop, segs, at + 1, nextHops, out leaf);
        if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind is JsonValueKind.Object or JsonValueKind.True)
            return TryResolve(root, extra, segs, at + 1, nextHops, out leaf);
        // A schema with no structure keywords (z.unknown / z.any) accepts anything.
        if (schema.EnumerateObject().All(p => p.Name is "description" or "title" or "default" or "$comment"))
            return true;
        return false;
    }

    /// <summary>Resolves a local <c>$ref</c> (<c>#</c>, <c>#/$defs/X</c>, <c>#/definitions/X</c>).</summary>
    private static bool Deref(JsonElement root, ref JsonElement schema, int hops, out int nextHops)
    {
        nextHops = hops;
        while (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var r) && r.ValueKind == JsonValueKind.String)
        {
            if (++nextHops > MaxRefHops) return false;
            var target = Pointer(root, r.GetString()!);
            if (target is null) return false;
            schema = target.Value;
        }
        return true;
    }

    private static JsonElement? Pointer(JsonElement root, string reference)
    {
        if (reference == "#") return root;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var cur = root;
        foreach (var raw in reference[2..].Split('/'))
        {
            var key = raw.Replace("~1", "/").Replace("~0", "~");
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out cur)) return null;
        }
        return cur;
    }

    /// <summary>The values a literal-like field (enum, const, boolean, or unions of those) can take; null otherwise.</summary>
    public static List<JsonElement>? LiteralValues(JsonElement root, JsonElement field, int hops = 0)
    {
        if (field.ValueKind != JsonValueKind.Object || !Deref(root, ref field, hops, out var next)) return null;
        if (field.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array) return en.EnumerateArray().ToList();
        if (field.TryGetProperty("const", out var c)) return [c];
        if (field.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "boolean")
        {
            using var doc = JsonDocument.Parse("[true,false]");
            return doc.RootElement.Clone().EnumerateArray().ToList();
        }
        var alts = field.TryGetProperty("anyOf", out var a) ? a : field.TryGetProperty("oneOf", out var o) ? o : default;
        if (alts.ValueKind != JsonValueKind.Array) return null;
        var all = new List<JsonElement>();
        foreach (var alt in alts.EnumerateArray())
        {
            var l = LiteralValues(root, alt, next);
            if (l is null) return null;
            all.AddRange(l);
        }
        return all;
    }

    /// <summary>Primitive JSON types a schema admits ("integer" folded into "number"); null = unknown/any.</summary>
    public static HashSet<string>? TypesOf(JsonElement root, JsonElement schema, int hops = 0)
    {
        if (schema.ValueKind != JsonValueKind.Object || !Deref(root, ref schema, hops, out var next)) return null;
        if (schema.TryGetProperty("const", out var c)) return [KindOf(c)];
        if (schema.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
            return en.EnumerateArray().Select(KindOf).ToHashSet();
        if (schema.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) return [Normalize(t.GetString()!)];
            if (t.ValueKind == JsonValueKind.Array)
                return t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Normalize(x.GetString()!)).ToHashSet();
        }
        foreach (var combinator in (string[])["anyOf", "oneOf"])
        {
            if (!schema.TryGetProperty(combinator, out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
            var union = new HashSet<string>();
            foreach (var b in branches.EnumerateArray())
            {
                var bt = TypesOf(root, b, next);
                if (bt is null) return null;
                union.UnionWith(bt);
            }
            return union;
        }
        return null;
    }

    public static string KindOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };

    private static string Normalize(string type) => type == "integer" ? "number" : type;

    /// <summary>Lenient compatibility: incompatible only when both are known and share no type.</summary>
    public static bool Overlaps(HashSet<string>? a, HashSet<string>? b) => a is null || b is null || a.Overlaps(b);

    /// <summary>The object shape of a schema (properties, required, whether extra keys are allowed); null if not a plain object schema.</summary>
    public static ObjectShape? ObjectShapeOf(JsonElement root, JsonElement schema, int hops = 0)
    {
        if (schema.ValueKind != JsonValueKind.Object || !Deref(root, ref schema, hops, out var next)) return null;
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var closed = schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False;
            var properties = props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            return new ObjectShape(properties, required, closed);
        }
        // nullable object: anyOf [{object}, {type: null}] → the single object branch
        foreach (var combinator in (string[])["anyOf", "oneOf"])
        {
            if (!schema.TryGetProperty(combinator, out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
            var shapes = branches.EnumerateArray().Select(b => ObjectShapeOf(root, b, next)).Where(s => s is not null).ToList();
            return shapes.Count == 1 ? shapes[0] : null;
        }
        return null;
    }

    public sealed record ObjectShape(IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlySet<string> Required, bool Closed);
}
