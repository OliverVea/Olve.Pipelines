using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// The harness envelope (<c>work/output.json</c>, also the stdout result block):
/// <code>
/// { "status": "succeeded", "output": … }
/// { "status": "skipped",   "reason": "…" }
/// { "status": "failed",    "error": { "kind": "handler" | "input" | "output", "message": "…" } }
/// </code>
/// Parsed strictly — it is untrusted step output (spec §8).
/// </summary>
public abstract record HarnessEnvelope
{
    /// <param name="OutputJson">The raw <c>output</c> JSON value (<c>null</c> for steps without an output model).</param>
    public sealed record Succeeded(string OutputJson) : HarnessEnvelope
    {
        /// <summary>The output JSON, or null when the step has no output (<c>"output": null</c>).</summary>
        public string? OutputOrNull => OutputJson == "null" ? null : OutputJson;
    }

    public sealed record Skipped(string Reason) : HarnessEnvelope;

    /// <param name="Kind"><c>handler</c>, <c>input</c> or <c>output</c>.</param>
    public sealed record Failed(string Kind, string Message) : HarnessEnvelope;

    private static readonly string[] FailureKinds = ["handler", "input", "output"];

    /// <summary>Default cap on the envelope text: room for an indented output at the compact cap.</summary>
    public const int DefaultMaxBytes = 4 * AttemptService.MaxOutputJsonBytes;

    public static Result<HarnessEnvelope> Parse(string json, int maxBytes = DefaultMaxBytes)
    {
        if (Encoding.UTF8.GetByteCount(json) > maxBytes)
            return Fail("envelope exceeds {0} bytes", maxBytes);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException e)
        {
            return Fail("envelope is not valid JSON: {0}", e.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("envelope is not a JSON object");
            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                return Fail("envelope has no string 'status'");

            switch (status.GetString())
            {
                case "succeeded":
                    if (!OnlyKeys(root, "status", "output"))
                        return Fail("succeeded envelope must have exactly 'status' and 'output'");
                    // output.json is canonical *indented* JSON; cap and store the compact form.
                    var output = Compact(root.GetProperty("output"));
                    if (Encoding.UTF8.GetByteCount(output) > AttemptService.MaxOutputJsonBytes)
                        return Fail("output exceeds {0} bytes", AttemptService.MaxOutputJsonBytes);
                    return new Succeeded(output);

                case "skipped":
                    if (!OnlyKeys(root, "status", "reason") || root.GetProperty("reason").ValueKind != JsonValueKind.String)
                        return Fail("skipped envelope must have exactly 'status' and a string 'reason'");
                    return new Skipped(root.GetProperty("reason").GetString()!);

                case "failed":
                    if (!OnlyKeys(root, "status", "error"))
                        return Fail("failed envelope must have exactly 'status' and 'error'");
                    var error = root.GetProperty("error");
                    if (error.ValueKind != JsonValueKind.Object || !OnlyKeys(error, "kind", "message")
                        || error.GetProperty("kind").ValueKind != JsonValueKind.String
                        || error.GetProperty("message").ValueKind != JsonValueKind.String)
                        return Fail("failed envelope 'error' must be {{kind, message}} strings");
                    var kind = error.GetProperty("kind").GetString()!;
                    if (!FailureKinds.Contains(kind))
                        return Fail("unknown failure kind '{0}'", kind);
                    return new Failed(kind, error.GetProperty("message").GetString()!);

                default:
                    return Fail("unknown envelope status '{0}'", status.GetString()!);
            }
        }
    }

    private static string Compact(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool OnlyKeys(JsonElement obj, params string[] keys)
    {
        var seen = 0;
        foreach (var p in obj.EnumerateObject())
        {
            if (!keys.Contains(p.Name)) return false;
            seen++;
        }
        return seen == keys.Length;
    }

    private static Result<HarnessEnvelope> Fail(string format, params object[] args)
        => Result.Failure<HarnessEnvelope>(new ResultProblem("Invalid harness envelope: " + format, args));
}
