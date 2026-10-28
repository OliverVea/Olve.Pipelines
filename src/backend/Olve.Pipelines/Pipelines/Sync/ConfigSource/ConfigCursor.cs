using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

/// <summary>
/// The paths whose commits make up a binding's config: the bound directory plus the local path
/// dependencies (<c>file:</c> / <c>link:</c>) of its <c>package.json</c>. A TypeScript pipeline builds
/// against those (e.g. <c>"@olivervea/pipelines": "file:../sdk"</c>), so a commit touching only them must
/// still rebuild the step binary.
/// </summary>
public static class LocalPathDependencies
{
    private static readonly string[] DependencySections = ["dependencies", "devDependencies", "optionalDependencies"];
    private static readonly string[] LocalPrefixes = ["file:", "link:"];

    /// <summary>
    /// Repo-relative paths (no leading/trailing <c>/</c>; <c>""</c> = the repo root) of the local path
    /// dependencies in <paramref name="packageJson"/>, resolved relative to <paramref name="bindingPath"/>.
    /// Absolute specs, specs escaping the repo, and paths inside the bound directory (already watched)
    /// are dropped; malformed JSON yields none. Distinct, in declaration order.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string bindingPath, string packageJson)
    {
        if (Normalize(bindingPath) is not { } root)
            return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(packageJson);
        }
        catch (JsonException)
        {
            return [];
        }

        var result = new List<string>();
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            foreach (var section in DependencySections)
            {
                if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var dep in deps.EnumerateObject())
                {
                    if (dep.Value.ValueKind != JsonValueKind.String || ResolveSpec(root, dep.Value.GetString()!) is not { } path)
                        continue;
                    if (!result.Contains(path, StringComparer.Ordinal))
                        result.Add(path);
                }
            }
        }

        return result;
    }

    private static string? ResolveSpec(string root, string spec)
    {
        var prefix = LocalPrefixes.FirstOrDefault(p => spec.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
            return null;

        var target = spec[prefix.Length..];
        if (target.Length == 0 || target.StartsWith('/') || target.Contains('\\') || target.Contains(':'))
            return null;

        if (Normalize(root.Length == 0 ? target : $"{root}/{target}") is not { } path)
            return null; // escapes the repo
        if (path == root || (root.Length > 0 && path.StartsWith(root + "/", StringComparison.Ordinal)))
            return null; // inside the bound directory: already watched

        return path;
    }

    /// <summary>Collapses <c>.</c>/<c>..</c>/empty segments; null when the path climbs above the repo root.</summary>
    internal static string? Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                        return null;
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        return string.Join('/', segments);
    }
}

/// <summary>The newest commit touching one watched path, with the ETag of the request that found it.</summary>
public sealed record PathCursor(string Path, string ETag, string Sha, DateTimeOffset Date);

/// <summary>
/// The config cursor across every watched path, carried between polls as the opaque "ETag" of
/// <see cref="IConfigSource.FetchConfigAsync"/> so each path's conditional request can be replayed.
/// The first entry is the bound directory.
/// </summary>
public sealed record ConfigCursorState(IReadOnlyList<PathCursor> Paths)
{
    private const string Prefix = "olve-cursor:v1:";

    /// <summary>The config commit: the newest commit across the watched paths (ties go to the earlier entry).</summary>
    public string Sha => Newest(Paths).Sha;

    public PathCursor? Find(string path) => Paths.FirstOrDefault(p => p.Path == path);

    public static PathCursor Newest(IReadOnlyList<PathCursor> paths)
    {
        var newest = paths[0];
        foreach (var p in paths.Skip(1))
            if (p.Date > newest.Date)
                newest = p;
        return newest;
    }

    public string Encode()
        => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, ConfigCursorJsonContext.Default.ConfigCursorState)));

    /// <summary>Null for anything this did not encode (e.g. absent, or a bare GitHub ETag).</summary>
    public static ConfigCursorState? Decode(string? etag)
    {
        if (etag is null || !etag.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(etag[Prefix.Length..]));
            return JsonSerializer.Deserialize(json, ConfigCursorJsonContext.Default.ConfigCursorState) is { Paths.Count: > 0 } state ? state : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigCursorState))]
internal partial class ConfigCursorJsonContext : JsonSerializerContext;
