using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// Writes manifest DTOs back to JSON in the manifest encoding (the inverse of <see cref="ManifestParser"/>).
/// Combine with <see cref="CanonicalJson"/> for canonical text and hashes; a parsed describe output
/// round-trips to its exact bytes.
/// </summary>
public static class ManifestWriter
{
    public static string ToCanonicalJson(Manifest manifest) => CanonicalJson.Serialize(ToElement(w => Write(w, manifest)));

    public static JsonElement ToElement(Manifest manifest) => ToElement(w => Write(w, manifest));

    public static JsonElement ToElement(StepDescriptor step) => ToElement(w => Write(w, step));

    public static JsonElement ToElement(ModelSchemas schemas) => ToElement(w => Write(w, schemas));

    public static JsonElement ToElement(Mapping mapping) => ToElement(w => Write(w, mapping));

    /// <summary>Builds a detached JsonElement with a Utf8JsonWriter (AOT-safe, no serializer).</summary>
    public static JsonElement ToElement(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            write(writer);
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    public static void Write(Utf8JsonWriter w, Manifest m)
    {
        w.WriteStartObject();
        w.WriteString("apiVersion", m.ApiVersion);
        w.WriteString("name", m.Name);
        if (m.Description is not null) w.WriteString("description", m.Description);
        w.WriteString("input", m.Input);
        w.WriteStartObject("harness");
        w.WriteString("bun", m.Harness.Bun);
        w.WriteString("sdk", m.Harness.Sdk);
        w.WriteString("sdkHash", m.Harness.SdkHash);
        w.WriteEndObject();
        w.WriteStartObject("models");
        foreach (var (name, schemas) in m.Models)
        {
            w.WritePropertyName(name);
            Write(w, schemas);
        }
        w.WriteEndObject();
        w.WriteStartArray("secrets");
        foreach (var s in m.Secrets)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("description", s.Description);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("triggers");
        foreach (var t in m.Triggers) Write(w, t);
        w.WriteEndArray();
        w.WriteStartArray("stages");
        foreach (var s in m.Stages) Write(w, s);
        w.WriteEndArray();
        w.WriteStartArray("failureHandlers");
        foreach (var fh in m.FailureHandlers)
        {
            w.WriteStartObject();
            w.WritePropertyName("step");
            Write(w, fh.Step);
            if (fh.Steps is null) w.WriteNull("steps");
            else WriteStrings(w, "steps", fh.Steps);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, ModelSchemas s)
    {
        w.WriteStartObject();
        w.WritePropertyName("output");
        s.Output.WriteTo(w);
        if (s.Input is { } input)
        {
            w.WritePropertyName("input");
            input.WriteTo(w);
        }
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, ManifestTrigger t)
    {
        w.WriteStartObject();
        switch (t)
        {
            case PushTrigger:
                w.WriteString("kind", "push");
                w.WriteString("name", PushTrigger.PushName);
                break;
            case PollTrigger p:
                w.WriteString("kind", "poll");
                w.WriteString("name", p.Name);
                w.WriteString("url", p.Url);
                w.WriteStartObject("headers");
                foreach (var (k, v) in p.Headers) w.WriteString(k, v);
                w.WriteEndObject();
                WriteStrings(w, "secrets", p.Secrets);
                w.WriteString("valuePath", p.ValuePath);
                w.WriteNumber("intervalSeconds", p.IntervalSeconds);
                break;
            case WebhookTrigger h:
                w.WriteString("kind", "webhook");
                w.WriteString("name", h.Name);
                w.WriteStartObject("verify");
                switch (h.Verify)
                {
                    case HmacSha256TriggerVerify hmac:
                        w.WriteString("scheme", "hmac-sha256");
                        w.WriteString("secret", hmac.Secret);
                        w.WriteString("header", hmac.Header);
                        w.WriteString("prefix", hmac.Prefix);
                        break;
                    case BearerTriggerVerify bearer:
                        w.WriteString("scheme", "bearer");
                        w.WriteString("secret", bearer.Secret);
                        break;
                }
                w.WriteEndObject();
                break;
        }
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, ManifestStage s)
    {
        w.WriteStartObject();
        switch (s)
        {
            case StepsStage ss:
                w.WriteStartArray("steps");
                foreach (var step in ss.Steps) Write(w, step);
                w.WriteEndArray();
                break;
            case WhenStage ws:
                w.WriteStartObject("when");
                foreach (var (field, values) in ws.When)
                {
                    w.WriteStartArray(field);
                    foreach (var v in values) v.WriteTo(w);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteStartArray("stages");
                foreach (var inner in ws.Stages) Write(w, inner);
                w.WriteEndArray();
                break;
        }
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, StepDescriptor s)
    {
        w.WriteStartObject();
        w.WriteString("name", s.Name);
        w.WriteString("description", s.Description);
        w.WriteString("kind", s.Kind);
        w.WriteString("image", s.Image);
        w.WriteStartObject("inputTypes");
        foreach (var (key, type) in s.InputTypes)
        {
            w.WritePropertyName(key);
            Write(w, type);
        }
        w.WriteEndObject();
        w.WriteStartObject("input");
        foreach (var (key, mapping) in s.Input)
        {
            w.WritePropertyName(key);
            Write(w, mapping);
        }
        w.WriteEndObject();
        if (s.Output is null) w.WriteNull("output");
        else w.WriteString("output", s.Output);
        WriteStrings(w, "secrets", s.Secrets);
        w.WriteString("handlerHash", s.HandlerHash);
        if (s.TimeoutSeconds is { } timeout) w.WriteNumber("timeoutSeconds", timeout);
        if (s.Retries is { } retries) w.WriteNumber("retries", retries);
        if (s.Config is not null)
        {
            w.WriteStartObject("config");
            foreach (var (k, v) in s.Config)
            {
                w.WritePropertyName(k);
                v.WriteTo(w);
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, InputType t)
    {
        w.WriteStartObject();
        switch (t)
        {
            case ModelInputType m:
                w.WriteString("model", m.Model);
                w.WriteBoolean("optional", m.Optional);
                break;
            case SchemaInputType s:
                w.WritePropertyName("schema");
                Write(w, s.Schema);
                break;
        }
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, Mapping m)
    {
        w.WriteStartObject();
        switch (m)
        {
            case RefMapping r:
                w.WriteString("ref", r.Ref);
                break;
            case ConstMapping c:
                w.WritePropertyName("const");
                c.Value.WriteTo(w);
                break;
            case ObjectMapping o:
                foreach (var (k, v) in o.Fields)
                {
                    w.WritePropertyName(k);
                    Write(w, v);
                }
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
