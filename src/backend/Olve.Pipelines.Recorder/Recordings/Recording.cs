using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Recorder.Recordings;

public sealed record RecordedRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers);

/// <param name="Body">JSON as the system sent it, a string for anything else, null for an empty body.</param>
public sealed record RecordedResponse(int Status, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    public string BodyText => Body switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => Body.ToJsonString(),
    };
}

/// <summary>One exchange captured from a real system.</summary>
/// <param name="Source">The instance it came from.</param>
/// <param name="SystemVersion">The version that instance reported.</param>
/// <param name="Elided">Fields of the response body removed at capture, which tests must not depend on.</param>
public sealed record Recording(
    string System,
    string Name,
    string Source,
    DateOnly CapturedAt,
    string SystemVersion,
    IReadOnlyList<string> Elided,
    RecordedRequest Request,
    RecordedResponse Response)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Id => $"{System}/{Name}";

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["system"] = System,
            ["name"] = Name,
            ["source"] = Source,
            ["capturedAt"] = CapturedAt.ToString("yyyy-MM-dd"),
            ["systemVersion"] = SystemVersion,
            ["elided"] = string.Join(", ", Elided),
            ["request"] = new JsonObject
            {
                ["method"] = Request.Method,
                ["url"] = Request.Url,
                ["headers"] = ToObject(Request.Headers),
            },
            ["response"] = new JsonObject
            {
                ["status"] = Response.Status,
                ["headers"] = ToObject(Response.Headers),
                ["body"] = Response.Body?.DeepClone(),
            },
        };
        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary>Throws <see cref="InvalidDataException"/> on anything without its capture metadata.</summary>
    public static Recording Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("not a JSON object");
        var request = Member(root, "request").AsObject();
        var response = Member(root, "response").AsObject();
        return new Recording(
            Text(root, "system"),
            Text(root, "name"),
            Text(root, "source"),
            DateOnly.ParseExact(Text(root, "capturedAt"), "yyyy-MM-dd"),
            Text(root, "systemVersion"),
            Member(root, "elided").GetValue<string>().Split(", ", StringSplitOptions.RemoveEmptyEntries),
            new RecordedRequest(Text(request, "method"), Text(request, "url"), ToDictionary(Member(request, "headers"))),
            new RecordedResponse(Member(response, "status").GetValue<int>(), ToDictionary(Member(response, "headers")), response["body"]?.DeepClone()));
    }

    private static JsonNode Member(JsonObject parent, string name) =>
        parent[name] ?? throw new InvalidDataException($"no {name}");

    private static string Text(JsonObject parent, string name) =>
        Member(parent, name).GetValue<string>() is { Length: > 0 } text ? text : throw new InvalidDataException($"empty {name}");

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> headers) =>
        new(headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => KeyValuePair.Create<string, JsonNode?>(h.Key, h.Value)));

    private static Dictionary<string, string> ToDictionary(JsonNode headers) =>
        headers.AsObject().ToDictionary(h => h.Key, h => h.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
}
