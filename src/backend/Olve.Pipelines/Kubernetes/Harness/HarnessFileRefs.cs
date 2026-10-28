using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared.Storage;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// A file reference as it appears in step inputs/outputs (src/sdk/README.md "File references"):
/// <c>{ run, step, attempt, path, sha256 }</c>. A trailing <c>/</c> on <see cref="Path"/> marks a directory,
/// stored as <c>&lt;path minus /&gt;.tar</c>.
/// </summary>
public sealed record HarnessFileRef(int Run, string Step, int Attempt, string Path, string Sha256)
{
    public bool IsDirectory => Path.EndsWith('/');

    /// <summary>The stored object's path under <c>files/</c>: <c>path</c>, or <c>path minus /</c> + <c>.tar</c> for a directory.</summary>
    public string ObjectPath => IsDirectory ? Path[..^1] + ".tar" : Path;

    /// <summary>The <c>OLVE_FILE_URLS</c> key: <c>&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/&lt;path&gt;</c> (path exactly as in the ref).</summary>
    public string UrlKey => string.Create(CultureInfo.InvariantCulture, $"{Run}/{Step}/{Attempt}/{Path}");
}

public static partial class HarnessFileRefs
{
    /// <summary>
    /// <c>OLVE_FILE_URLS</c> goes into one env var, and Linux caps a single env string at 128 KiB
    /// (MAX_ARG_STRLEN). Stay well below it.
    /// </summary>
    public const int MaxFileUrlsJsonBytes = 100 * 1024;

    // src/sdk/src/core/models.ts FILE_PATH: segments without / \ NUL, not . or .., optional trailing / (directory).
    [GeneratedRegex(@"^(?:(?!\.\.?(?:/|$))[^/\\\x00]+/)*(?!\.\.?(?:/|$))[^/\\\x00]+/?$")]
    private static partial Regex FilePathRegex();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex StepNameRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    public static bool IsValidPath(string path) => FilePathRegex().IsMatch(path);

    /// <summary>Validates a ref's fields (untrusted: they become storage keys).</summary>
    public static Result Validate(HarnessFileRef fileRef)
    {
        if (fileRef.Run < 0 || fileRef.Attempt < 1)
            return new ResultProblem("File ref '{0}' has an invalid run or attempt number.", fileRef.UrlKey);
        if (!StepNameRegex().IsMatch(fileRef.Step))
            return new ResultProblem("File ref has an invalid step name '{0}'.", fileRef.Step);
        if (!FilePathRegex().IsMatch(fileRef.Path))
            return new ResultProblem("File ref '{0}/{1}/{2}' has an invalid path.", fileRef.Run, fileRef.Step, fileRef.Attempt);
        if (!Sha256Regex().IsMatch(fileRef.Sha256))
            return new ResultProblem("File ref '{0}' has an invalid sha256.", fileRef.UrlKey);
        return Result.Success();
    }

    /// <summary>
    /// Finds every file ref in a JSON document (an attempt's assembled input): any object with exactly the
    /// keys <c>run</c>, <c>step</c>, <c>attempt</c>, <c>path</c>, <c>sha256</c> of the right JSON types.
    /// Shape-based, so it needs no schema; refs are de-duplicated by <see cref="HarnessFileRef.UrlKey"/>.
    /// Values are not validated here — see <see cref="Validate"/>.
    /// </summary>
    public static IReadOnlyList<HarnessFileRef> Find(JsonElement root)
    {
        var found = new Dictionary<string, HarnessFileRef>(StringComparer.Ordinal);
        Walk(root, found, depth: 0);
        return found.Values.OrderBy(r => r.UrlKey, StringComparer.Ordinal).ToList();
    }

    private static void Walk(JsonElement element, Dictionary<string, HarnessFileRef> found, int depth)
    {
        if (depth > 64) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryRead(element, out var fileRef))
                {
                    found.TryAdd(fileRef.UrlKey, fileRef);
                    return;
                }
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, found, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, found, depth + 1);
                break;
        }
    }

    private static bool TryRead(JsonElement obj, out HarnessFileRef fileRef)
    {
        fileRef = null!;
        int? run = null, attempt = null;
        string? step = null, path = null, sha = null;
        var count = 0;
        foreach (var p in obj.EnumerateObject())
        {
            count++;
            switch (p.Name)
            {
                case "run" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var r): run = r; break;
                case "attempt" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var a): attempt = a; break;
                case "step" when p.Value.ValueKind == JsonValueKind.String: step = p.Value.GetString(); break;
                case "path" when p.Value.ValueKind == JsonValueKind.String: path = p.Value.GetString(); break;
                case "sha256" when p.Value.ValueKind == JsonValueKind.String: sha = p.Value.GetString(); break;
                default: return false;
            }
        }
        if (count != 5 || run is null || attempt is null || step is null || path is null || sha is null)
            return false;
        fileRef = new HarnessFileRef(run.Value, step, attempt.Value, path, sha);
        return true;
    }

    /// <summary>
    /// Builds the <c>OLVE_FILE_URLS</c> map: <c>&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/&lt;path&gt;</c> → pre-signed GET of
    /// the stored object (<see cref="HarnessStorageKeys.FileObjectKey"/>). The ref's own run/step/attempt
    /// address the object — continues and reruns reference earlier runs. Fails on an invalid ref.
    /// </summary>
    public static async Task<Result<IReadOnlyDictionary<string, string>>> BuildUrlMapAsync(
        Id<Pipeline> pipelineId,
        IEnumerable<HarnessFileRef> refs,
        S3UrlPresigner presigner,
        TimeSpan expiry,
        CancellationToken ct = default)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var fileRef in refs)
        {
            if (Validate(fileRef).TryPickProblems(out var problems))
                return Result.Failure<IReadOnlyDictionary<string, string>>(problems);
            if (map.ContainsKey(fileRef.UrlKey))
                continue;
            map[fileRef.UrlKey] = await presigner.PresignGetAsync(HarnessStorageKeys.FileObjectKey(pipelineId, fileRef), expiry, ct);
        }
        return map;
    }

    /// <summary>Serializes the URL map as a compact JSON object with sorted keys (the env var value).</summary>
    public static string ToJson(IReadOnlyDictionary<string, string> urls)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var (key, url) in urls.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                writer.WriteString(key, url);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
