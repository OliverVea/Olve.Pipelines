using System.Text;
using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// Parses manifest JSON into <see cref="Manifest"/>, enforcing the schema's <em>shape</em> (types, required
/// and unknown properties, discriminators). Semantic checks (names, refs, secrets, …) are
/// <see cref="ManifestValidator"/>'s job. All shape problems are reported together.
/// </summary>
public static class ManifestParser
{
    /// <summary>Raw manifest size cap (UTF-8 bytes).</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Maximum JSON nesting depth.</summary>
    public const int MaxDepth = 64;

    public static Result<Manifest> Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    public static Result<Manifest> Parse(ReadOnlyMemory<byte> utf8)
    {
        if (TryParse(utf8, out var manifest, out var issues)) return manifest;
        return Result.Failure<Manifest>(issues.Select(i => i.ToProblem()));
    }

    /// <summary>Like <see cref="Parse(ReadOnlyMemory{byte})"/>, returning structured issues.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> utf8, out Manifest manifest, out IReadOnlyList<ManifestIssue> issues)
    {
        manifest = null!;
        var ctx = new Ctx();
        issues = ctx.Issues;

        if (utf8.Length > MaxBytes)
        {
            ctx.Error(ManifestIssueCodes.TooLarge, "$", $"manifest is {utf8.Length} bytes; the limit is {MaxBytes}");
            return false;
        }
        var bytes = utf8.Span.StartsWith("﻿"u8) ? utf8[3..] : utf8;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException e)
        {
            ctx.Error(ManifestIssueCodes.InvalidJson, "$", $"not valid JSON (max depth {MaxDepth}): {e.Message}");
            return false;
        }

        using (doc)
        {
            PreWalk(ctx, doc.RootElement, "$");
            if (ctx.Issues.Count > 0) return false;
            var m = ParseManifest(ctx, doc.RootElement);
            if (ctx.Issues.Count > 0 || m is null) return false;
            manifest = m;
            return true;
        }
    }

    // ── clean-input pass: duplicate keys, non-finite numbers, undecodable strings ──
    private static void PreWalk(Ctx ctx, JsonElement e, string path)
    {
        try
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject())
                    {
                        var child = Ctx.Child(path, p.Name);
                        if (!seen.Add(p.Name)) ctx.Error(ManifestIssueCodes.DuplicateKey, child, $"duplicate key '{p.Name}'");
                        PreWalk(ctx, p.Value, child);
                    }
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in e.EnumerateArray()) PreWalk(ctx, item, $"{path}[{i++}]");
                    break;
                case JsonValueKind.String:
                    _ = e.GetString();
                    break;
                case JsonValueKind.Number:
                    if (!e.TryGetDouble(out var d) || !double.IsFinite(d))
                        ctx.Error(ManifestIssueCodes.InvalidValue, path, "number is out of range");
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            // Boundary: System.Text.Json throws on strings that are not valid UTF-16 (e.g. lone surrogates).
            ctx.Error(ManifestIssueCodes.InvalidValue, path, "string is not valid Unicode (lone surrogate escape?)");
        }
    }

    // ── manifest ──
    private static Manifest? ParseManifest(Ctx ctx, JsonElement e)
    {
        const string p = "$";
        if (!ctx.Object(e, p, ["apiVersion", "name", "input", "harness", "models", "secrets", "triggers", "stages", "failureHandlers"], ["description"]))
            return null;

        var apiVersion = ctx.String(e, "apiVersion", p);
        if (apiVersion is not null && apiVersion != Manifest.SupportedApiVersion)
            ctx.Error(ManifestIssueCodes.UnsupportedApiVersion, "apiVersion", $"apiVersion '{apiVersion}' is not supported (expected '{Manifest.SupportedApiVersion}')");
        var name = ctx.String(e, "name", p);
        var description = ctx.OptionalString(e, "description", p);
        var input = ctx.String(e, "input", p);
        var harness = ParseHarness(ctx, e, "harness");
        var models = ctx.Dictionary(e, "models", p, (v, path) => ParseModelSchemas(ctx, v, path));
        var secrets = ctx.Array(e, "secrets", p, (v, path) => ParseSecret(ctx, v, path));
        var triggers = ctx.Array(e, "triggers", p, (v, path) => ParseTrigger(ctx, v, path));
        var stages = ctx.Array(e, "stages", p, (v, path) => ParseStage(ctx, v, path));
        var failureHandlers = ctx.Array(e, "failureHandlers", p, (v, path) => ParseFailureHandler(ctx, v, path));

        if (apiVersion is null || name is null || input is null || harness is null || models is null || secrets is null
            || triggers is null || stages is null || failureHandlers is null)
            return null;
        return new Manifest(apiVersion, name, description, input, harness, models, secrets, triggers, stages, failureHandlers);
    }

    private static ManifestHarness? ParseHarness(Ctx ctx, JsonElement parent, string path)
    {
        var e = parent.GetProperty("harness");
        if (!ctx.Object(e, path, ["bun", "sdk", "sdkHash"], [])) return null;
        var bun = ctx.String(e, "bun", path);
        var sdk = ctx.String(e, "sdk", path);
        var sdkHash = ctx.String(e, "sdkHash", path);
        return bun is null || sdk is null || sdkHash is null ? null : new ManifestHarness(bun, sdk, sdkHash);
    }

    private static ModelSchemas? ParseModelSchemas(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Object(e, path, ["output"], ["input"])) return null;
        var output = e.GetProperty("output");
        var ok = ctx.Expect(output, JsonValueKind.Object, Ctx.Child(path, "output"), "a JSON Schema object");
        JsonElement? input = null;
        if (e.TryGetProperty("input", out var i))
        {
            ok &= ctx.Expect(i, JsonValueKind.Object, Ctx.Child(path, "input"), "a JSON Schema object");
            input = i.Clone();
        }
        return ok ? new ModelSchemas(output.Clone(), input) : null;
    }

    private static ManifestSecret? ParseSecret(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Object(e, path, ["name", "description"], [])) return null;
        var name = ctx.String(e, "name", path);
        var description = ctx.String(e, "description", path);
        return name is null || description is null ? null : new ManifestSecret(name, description);
    }

    private static ManifestTrigger? ParseTrigger(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Expect(e, JsonValueKind.Object, path, "an object")) return null;
        if (!e.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
        {
            ctx.Error(ManifestIssueCodes.MissingProperty, Ctx.Child(path, "kind"), "a trigger needs a string 'kind' ('push', 'poll' or 'webhook')");
            return null;
        }
        switch (kindEl.GetString())
        {
            case "push":
                if (!ctx.Object(e, path, ["kind", "name"], [])) return null;
                var pushName = ctx.String(e, "name", path);
                if (pushName is not null && pushName != PushTrigger.PushName)
                    ctx.Error(ManifestIssueCodes.InvalidValue, Ctx.Child(path, "name"), "a push trigger's name must be 'push'");
                return pushName == PushTrigger.PushName ? new PushTrigger() : null;
            case "poll":
                if (!ctx.Object(e, path, ["kind", "name", "url", "headers", "secrets", "valuePath", "intervalSeconds"], [])) return null;
                var name = ctx.String(e, "name", path);
                var url = ctx.String(e, "url", path);
                var headers = ctx.Dictionary(e, "headers", path, (v, hp) => ctx.Expect(v, JsonValueKind.String, hp, "a string") ? v.GetString() : null);
                var secrets = ctx.StringArray(e, "secrets", path);
                var valuePath = ctx.String(e, "valuePath", path);
                var interval = ctx.Integer(e, "intervalSeconds", path);
                if (name is null || url is null || headers is null || secrets is null || valuePath is null || interval is null) return null;
                return new PollTrigger(name, url, headers!, secrets, valuePath, interval.Value);
            case "webhook":
                if (!ctx.Object(e, path, ["kind", "name", "verify"], [])) return null;
                var hookName = ctx.String(e, "name", path);
                var verify = ParseWebhookVerify(ctx, e.GetProperty("verify"), Ctx.Child(path, "verify"));
                return hookName is null || verify is null ? null : new WebhookTrigger(hookName, verify);
            default:
                ctx.Error(ManifestIssueCodes.InvalidValue, Ctx.Child(path, "kind"), $"unknown trigger kind '{kindEl.GetString()}' (expected 'push', 'poll' or 'webhook')");
                return null;
        }
    }

    private static WebhookTriggerVerify? ParseWebhookVerify(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Expect(e, JsonValueKind.Object, path, "an object")) return null;
        if (!e.TryGetProperty("scheme", out var schemeEl) || schemeEl.ValueKind != JsonValueKind.String)
        {
            ctx.Error(ManifestIssueCodes.MissingProperty, Ctx.Child(path, "scheme"), "verify needs a string 'scheme' ('hmac-sha256' or 'bearer')");
            return null;
        }
        switch (schemeEl.GetString())
        {
            case "hmac-sha256":
                if (!ctx.Object(e, path, ["scheme", "secret", "header", "prefix"], [])) return null;
                var secret = ctx.String(e, "secret", path);
                var header = ctx.String(e, "header", path);
                var prefix = ctx.String(e, "prefix", path);
                return secret is null || header is null || prefix is null ? null : new HmacSha256TriggerVerify(secret, header, prefix);
            case "bearer":
                if (!ctx.Object(e, path, ["scheme", "secret"], [])) return null;
                var token = ctx.String(e, "secret", path);
                return token is null ? null : new BearerTriggerVerify(token);
            default:
                ctx.Error(ManifestIssueCodes.InvalidValue, Ctx.Child(path, "scheme"), $"unknown verify scheme '{schemeEl.GetString()}' (expected 'hmac-sha256' or 'bearer')");
                return null;
        }
    }

    private static ManifestStage? ParseStage(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Expect(e, JsonValueKind.Object, path, "an object")) return null;
        if (e.TryGetProperty("steps", out _))
        {
            if (!ctx.Object(e, path, ["steps"], [])) return null;
            var steps = ctx.Array(e, "steps", path, (v, sp) => ParseStep(ctx, v, sp));
            if (steps is null) return null;
            if (steps.Count == 0)
            {
                ctx.Error(ManifestIssueCodes.StageEmpty, Ctx.Child(path, "steps"), "a stage needs at least one step");
                return null;
            }
            return new StepsStage(steps);
        }
        if (e.TryGetProperty("when", out _) || e.TryGetProperty("stages", out _))
        {
            if (!ctx.Object(e, path, ["when", "stages"], [])) return null;
            var when = ctx.Dictionary(e, "when", path, (v, wp) => ParseWhenValues(ctx, v, wp));
            var stages = ctx.Array(e, "stages", path, (v, sp) => ParseStage(ctx, v, sp));
            return when is null || stages is null ? null : new WhenStage(when, stages);
        }
        ctx.Error(ManifestIssueCodes.MissingProperty, path, "a stage is either {steps} or {when, stages}");
        return null;
    }

    private static IReadOnlyList<JsonElement>? ParseWhenValues(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Expect(e, JsonValueKind.Array, path, "an array of values")) return null;
        var values = new List<JsonElement>();
        var i = 0;
        foreach (var v in e.EnumerateArray())
        {
            if (v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) values.Add(v.Clone());
            else ctx.Error(ManifestIssueCodes.WrongType, $"{path}[{i}]", "a when value must be a string, number or boolean");
            i++;
        }
        if (i == 0) ctx.Error(ManifestIssueCodes.InvalidValue, path, "a when field needs at least one value");
        return values.Count == i && i > 0 ? values : null;
    }

    private static ManifestFailureHandler? ParseFailureHandler(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Object(e, path, ["step", "steps"], [])) return null;
        var step = ParseStep(ctx, e.GetProperty("step"), Ctx.Child(path, "step"));
        var stepsEl = e.GetProperty("steps");
        IReadOnlyList<string>? steps = null;
        var stepsOk = true;
        if (stepsEl.ValueKind != JsonValueKind.Null)
        {
            steps = ctx.StringArray(e, "steps", path);
            stepsOk = steps is not null;
        }
        return step is null || !stepsOk ? null : new ManifestFailureHandler(step, steps);
    }

    private static StepDescriptor? ParseStep(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Object(e, path, ["name", "kind", "image", "inputTypes", "input", "output", "secrets", "handlerHash"], ["description", "timeoutSeconds", "retries", "config"]))
            return null;
        var name = ctx.String(e, "name", path);
        // Optional only so versions built before step descriptions still load.
        var description = e.TryGetProperty("description", out _) ? ctx.String(e, "description", path) : "";
        var kind = ctx.String(e, "kind", path);
        var image = ctx.String(e, "image", path);
        var inputTypes = ctx.Dictionary(e, "inputTypes", path, (v, ip) => ParseInputType(ctx, v, ip));
        var input = ctx.Dictionary(e, "input", path, (v, mp) => ParseMapping(ctx, v, mp));
        var outputEl = e.GetProperty("output");
        string? output = null;
        var outputOk = true;
        if (outputEl.ValueKind == JsonValueKind.String) output = outputEl.GetString();
        else if (outputEl.ValueKind != JsonValueKind.Null)
        {
            ctx.Error(ManifestIssueCodes.WrongType, Ctx.Child(path, "output"), "output must be a model name or null");
            outputOk = false;
        }
        var secrets = ctx.StringArray(e, "secrets", path);
        var handlerHash = ctx.String(e, "handlerHash", path);
        long? timeout = e.TryGetProperty("timeoutSeconds", out _) ? ctx.Integer(e, "timeoutSeconds", path) : null;
        var timeoutOk = !e.TryGetProperty("timeoutSeconds", out _) || timeout is not null;
        long? retries = e.TryGetProperty("retries", out _) ? ctx.Integer(e, "retries", path) : null;
        var retriesOk = !e.TryGetProperty("retries", out _) || retries is not null;
        IReadOnlyDictionary<string, JsonElement>? config = null;
        var configOk = true;
        if (e.TryGetProperty("config", out _))
        {
            var c = ctx.Dictionary(e, "config", path, (v, _) => (JsonElement?)v.Clone());
            configOk = c is not null;
            config = c?.ToDictionary(kv => kv.Key, kv => kv.Value!.Value, StringComparer.Ordinal);
        }

        if (name is null || description is null || kind is null || image is null || inputTypes is null || input is null || !outputOk || secrets is null
            || handlerHash is null || !timeoutOk || !retriesOk || !configOk)
            return null;
        return new StepDescriptor(name, description, kind, image, inputTypes!, input!, output, secrets, handlerHash, timeout, retries, config);
    }

    private static InputType? ParseInputType(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Expect(e, JsonValueKind.Object, path, "an object")) return null;
        if (e.TryGetProperty("model", out _))
        {
            if (!ctx.Object(e, path, ["model", "optional"], [])) return null;
            var model = ctx.String(e, "model", path);
            var optional = ctx.Boolean(e, "optional", path);
            return model is null || optional is null ? null : new ModelInputType(model, optional.Value);
        }
        if (e.TryGetProperty("schema", out var schema))
        {
            if (!ctx.Object(e, path, ["schema"], [])) return null;
            var s = ParseModelSchemas(ctx, schema, Ctx.Child(path, "schema"));
            return s is null ? null : new SchemaInputType(s);
        }
        ctx.Error(ManifestIssueCodes.MissingProperty, path, "an input type is either {model, optional} or {schema}");
        return null;
    }

    /// <summary>Only key 'ref' (string) = ref; only key 'const' = constant; any other object = nested.</summary>
    private static Mapping? ParseMapping(Ctx ctx, JsonElement e, string path)
    {
        if (!ctx.Expect(e, JsonValueKind.Object, path, "a mapping object ({ref}, {const} or nested)")) return null;
        var props = e.EnumerateObject().ToList();
        if (props.Count == 1 && props[0].Name == "ref" && props[0].Value.ValueKind == JsonValueKind.String)
            return new RefMapping(props[0].Value.GetString()!);
        if (props.Count == 1 && props[0].Name == "const")
            return new ConstMapping(props[0].Value.Clone());
        var fields = new Dictionary<string, Mapping>(StringComparer.Ordinal);
        var ok = true;
        foreach (var p in props)
        {
            var m = ParseMapping(ctx, p.Value, Ctx.Child(path, p.Name));
            if (m is null) ok = false;
            else fields[p.Name] = m;
        }
        return ok ? new ObjectMapping(fields) : null;
    }

    // ── helpers ──
    private sealed class Ctx
    {
        public List<ManifestIssue> Issues { get; } = [];

        public void Error(string code, string path, string message) =>
            Issues.Add(new ManifestIssue(code, ManifestIssueSeverity.Error, message, path));

        public static string Child(string path, string key)
        {
            var simple = key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
            var seg = simple ? key : "[" + JsonEncodedText.Encode(key).ToString() + "]";
            if (path == "$") return simple ? key : "$" + seg;
            return simple ? path + "." + seg : path + seg;
        }

        public bool Expect(JsonElement e, JsonValueKind kind, string path, string what)
        {
            if (e.ValueKind == kind) return true;
            Error(ManifestIssueCodes.WrongType, path, $"expected {what}, got {Describe(e.ValueKind)}");
            return false;
        }

        private static string Describe(JsonValueKind k) => k switch
        {
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            _ => "null",
        };

        /// <summary>Object with exactly the given required (+ optional) properties.</summary>
        public bool Object(JsonElement e, string path, string[] required, string[] optional)
        {
            if (!Expect(e, JsonValueKind.Object, path, "an object")) return false;
            var ok = true;
            foreach (var r in required)
            {
                if (e.TryGetProperty(r, out _)) continue;
                Error(ManifestIssueCodes.MissingProperty, Child(path, r), $"missing required property '{r}'");
                ok = false;
            }
            foreach (var p in e.EnumerateObject())
            {
                if (required.Contains(p.Name) || optional.Contains(p.Name)) continue;
                Error(ManifestIssueCodes.UnknownProperty, Child(path, p.Name), $"unknown property '{p.Name}'");
                ok = false;
            }
            return ok;
        }

        public string? String(JsonElement obj, string prop, string path)
        {
            var e = obj.GetProperty(prop);
            return Expect(e, JsonValueKind.String, Child(path, prop), "a string") ? e.GetString() : null;
        }

        public string? OptionalString(JsonElement obj, string prop, string path) =>
            obj.TryGetProperty(prop, out _) ? String(obj, prop, path) : null;

        public bool? Boolean(JsonElement obj, string prop, string path)
        {
            var e = obj.GetProperty(prop);
            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False) return e.GetBoolean();
            Error(ManifestIssueCodes.WrongType, Child(path, prop), $"expected a boolean, got {Describe(e.ValueKind)}");
            return null;
        }

        public long? Integer(JsonElement obj, string prop, string path)
        {
            var e = obj.GetProperty(prop);
            if (!Expect(e, JsonValueKind.Number, Child(path, prop), "an integer")) return null;
            if (e.TryGetInt64(out var v)) return v;
            Error(ManifestIssueCodes.WrongType, Child(path, prop), "expected an integer");
            return null;
        }

        public IReadOnlyList<T>? Array<T>(JsonElement obj, string prop, string path, Func<JsonElement, string, T?> item) where T : class
        {
            var e = obj.GetProperty(prop);
            var p = Child(path, prop);
            if (!Expect(e, JsonValueKind.Array, p, "an array")) return null;
            var list = new List<T>();
            var ok = true;
            var i = 0;
            foreach (var x in e.EnumerateArray())
            {
                var v = item(x, $"{p}[{i++}]");
                if (v is null) ok = false;
                else list.Add(v);
            }
            return ok ? list : null;
        }

        public IReadOnlyList<string>? StringArray(JsonElement obj, string prop, string path) =>
            Array(obj, prop, path, (x, p) => Expect(x, JsonValueKind.String, p, "a string") ? x.GetString() : null);

        public IReadOnlyDictionary<string, T>? Dictionary<T>(JsonElement obj, string prop, string path, Func<JsonElement, string, T?> item)
        {
            var e = obj.GetProperty(prop);
            var p = Child(path, prop);
            if (!Expect(e, JsonValueKind.Object, p, "an object")) return null;
            var dict = new Dictionary<string, T>(StringComparer.Ordinal);
            var ok = true;
            foreach (var x in e.EnumerateObject())
            {
                var v = item(x.Value, Child(p, x.Name));
                if (v is null) ok = false;
                else dict[x.Name] = v;
            }
            return ok ? dict : null;
        }
    }
}
