using System.Globalization;
using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// A file reference found in an attempt's input (spec §5.5): a value shaped
/// <c>{run, step, attempt, path, sha256}</c> (the output view of an <c>x-olve-kind: fileRef</c> schema).
/// </summary>
/// <param name="JsonPath">Where in <c>input.json</c> the ref sits (<c>$.artifact.image</c>), for diagnostics.</param>
public sealed record AttemptFileRef(int Run, string Step, int Attempt, string Path, string Sha256, string JsonPath)
{
    /// <summary>The <c>OLVE_FILE_URLS</c> key: <c>&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/&lt;path&gt;</c> (path verbatim).</summary>
    public string FileKey => string.Create(CultureInfo.InvariantCulture, $"{Run}/{Step}/{Attempt}/{Path}");

    /// <summary>The stored object's path: <c>path</c> for files, <c>dir.tar</c> for a directory ref <c>dir/</c>.</summary>
    public string ObjectPath => Path.EndsWith('/') ? Path.TrimEnd('/') + ".tar" : Path;

    /// <summary>The Garage key of the referenced object (same pipeline as the consuming attempt).</summary>
    public string StorageKey(Id<Pipeline> pipelineId) => StorageLayout.AttemptFileKey(pipelineId, Run, Step, Attempt, ObjectPath);
}

public static class AttemptFileRefs
{
    /// <summary>All distinct file refs in an input JSON document (detected by shape), in document order.</summary>
    public static IReadOnlyList<AttemptFileRef> Extract(string? inputJson)
    {
        if (string.IsNullOrEmpty(inputJson))
            return [];

        using var document = JsonDocument.Parse(inputJson);
        var refs = new List<AttemptFileRef>();
        Walk(document.RootElement, "$", refs);
        return refs.DistinctBy(r => r.FileKey).ToList();
    }

    private static void Walk(JsonElement element, string path, List<AttemptFileRef> refs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when TryRead(element, path, out var fileRef):
                refs.Add(fileRef);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, $"{path}.{property.Name}", refs);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    Walk(item, string.Create(CultureInfo.InvariantCulture, $"{path}[{i++}]"), refs);
                break;
        }
    }

    private static bool TryRead(JsonElement e, string path, out AttemptFileRef fileRef)
    {
        fileRef = null!;
        if (e.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Number && run.TryGetInt32(out var runNumber)
            && e.TryGetProperty("step", out var step) && step.ValueKind == JsonValueKind.String
            && e.TryGetProperty("attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number && attempt.TryGetInt32(out var attemptNumber)
            && e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            && e.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String)
        {
            fileRef = new AttemptFileRef(runNumber, step.GetString()!, attemptNumber, p.GetString()!, sha.GetString()!, path);
            return true;
        }

        return false;
    }
}
