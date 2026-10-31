using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Olve.Pipelines.Recorder.Recordings;

/// <summary>Takes secrets out of a capture before it reaches disk.</summary>
public static partial class Scrubber
{
    public const string Placeholder = "<scrubbed>";

    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "accesskey", "api_key", "apikey", "authorization", "client_secret", "cookie", "id_token",
        "password", "private_key", "refresh_token", "secret", "secretkey", "set-cookie", "token",
    };

    /// <summary>The private half of a JSON Web Key.</summary>
    private static readonly string[] PrivateKeyFields = ["d", "p", "q", "dp", "dq", "qi", "k"];

    private static readonly Regex[] SecretValues =
    [
        PemBlock(), GitHubToken(), GitHubPat(), BearerCredential(), BasicCredential(), Jwt(), PresignedSignature(), GarageKey(), UrlCredentials(), Email(),
    ];

    /// <summary>Only these reach a recording: everything else a system answers with is dropped unseen.</summary>
    public static IReadOnlyDictionary<string, string> KeepHeaders(IEnumerable<KeyValuePair<string, string>> headers, params string[] allowed) =>
        headers
            .Where(h => allowed.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => Scrub(h.Value));

    public static string Scrub(string text) =>
        SecretValues.Aggregate(text, (current, pattern) => pattern.Replace(current, Placeholder));

    /// <summary>A copy of <paramref name="node"/> with every secret field and value replaced.</summary>
    public static JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject item:
                var isKey = item.ContainsKey("kty");
                return new JsonObject(item.Select(p => KeyValuePair.Create(
                    p.Key,
                    SecretFields.Contains(p.Key) || (isKey && PrivateKeyFields.Contains(p.Key)) ? (JsonNode?)Placeholder : Scrub(p.Value))));
            case JsonArray list:
                return new JsonArray(list.Select(Scrub).ToArray());
            default:
                return node.GetValueKind() == System.Text.Json.JsonValueKind.String ? Scrub(node.GetValue<string>()) : node.DeepClone();
        }
    }

    /// <summary>What still looks like a secret in <paramref name="text"/>: the last check on anything written or committed.</summary>
    public static IReadOnlyList<string> Leaks(string text) =>
        SecretValues.SelectMany(p => p.Matches(text).Select(m => m.Value)).ToList();

    /// <summary>Removes <paramref name="paths"/> (dotted, applied to each item of a list).</summary>
    public static JsonNode? Elide(JsonNode? body, IEnumerable<string> paths)
    {
        var copy = body?.DeepClone();
        foreach (var path in paths)
            Remove(copy, path.Split('.'));
        return copy;
    }

    private static void Remove(JsonNode? node, ReadOnlySpan<string> path)
    {
        switch (node)
        {
            case JsonArray list:
                foreach (var item in list)
                    Remove(item, path);
                break;
            case JsonObject item when path.Length == 1:
                item.Remove(path[0]);
                break;
            case JsonObject item:
                Remove(item[path[0]], path[1..]);
                break;
        }
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----.*?-----END [A-Z0-9 ]+-----", RegexOptions.Singleline)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{20,}")]
    private static partial Regex GitHubPat();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerCredential();

    [GeneratedRegex(@"\bBasic\s+[A-Za-z0-9+/]{8,}={0,2}")]
    private static partial Regex BasicCredential();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"X-Amz-(Signature|Credential|Security-Token)=[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex PresignedSignature();

    [GeneratedRegex(@"\bGK[0-9a-f]{24}\b")]
    private static partial Regex GarageKey();

    [GeneratedRegex(@"://[^/\s:@""]+:[^/\s@""]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+")]
    private static partial Regex Email();
}
