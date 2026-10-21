using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>A step's entry in the local pool.</summary>
public sealed record PoolEntry(string Status, JsonNode? Output, string? Reason)
{
    public const string Succeeded = "succeeded";
    public const string Skipped = "skipped";
    public const string Failed = "failed";

    public bool IsSucceeded => Status == Succeeded;
}

/// <summary>
/// The local pool (<c>.olve/pool.json</c>): the pipeline input plus every step's latest local
/// outcome, so successive <c>pl dev run</c> calls chain like stages of a real run. Output files
/// live beside it in <c>.olve/files/&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/files/</c>, the layout the harness
/// reads through <c>OLVE_FILES_DIR</c>.
/// </summary>
public sealed class DevPool
{
    /// <summary>Local runs are run 0, attempt 1 (the harness defaults); file refs carry these.</summary>
    public const int Run = 0;
    public const int Attempt = 1;

    /// <summary>Indented, without escaping <c>+</c>/<c>&lt;</c>/non-ASCII (files and terminal, never HTML).</summary>
    public static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Indented variant of <see cref="CompactJson"/>.</summary>
    public static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonObject? Input { get; set; }

    public Dictionary<string, PoolEntry> Steps { get; } = new(StringComparer.Ordinal);

    public static Result<DevPool> Load(string path)
    {
        var pool = new DevPool();
        if (!File.Exists(path))
            return pool;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            return new ResultProblem("Local pool '{0}' is not valid JSON ({1}); delete it to start fresh.", path, ex.Message);
        }

        if (root is not JsonObject obj)
            return pool;

        pool.Input = obj["input"]?.DeepClone() as JsonObject;
        if (obj["steps"] is JsonObject steps)
        {
            foreach (var (name, entry) in steps)
            {
                if (entry is not JsonObject e || DevManifest.Str(e["status"]) is not { } status)
                    continue;
                pool.Steps[name] = new PoolEntry(status, e["output"]?.DeepClone(), DevManifest.Str(e["reason"]));
            }
        }

        return pool;
    }

    public void Save(string path)
    {
        var steps = new JsonObject();
        foreach (var (name, entry) in Steps)
        {
            var e = new JsonObject { ["status"] = entry.Status };
            if (entry.IsSucceeded)
                e["output"] = entry.Output?.DeepClone();
            if (entry.Reason is not null)
                e["reason"] = entry.Reason;
            steps[name] = e;
        }

        var root = new JsonObject
        {
            ["input"] = Input?.DeepClone(),
            ["steps"] = steps,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, root.ToJsonString(IndentedJson) + "\n");
    }

    /// <summary>
    /// Records a step's envelope (<c>work/output.json</c>). On success the attempt's
    /// <c>work/files/</c> is copied into the pool files tree so later steps can fetch its refs.
    /// </summary>
    public void Record(string step, JsonObject? envelope, DevWorkspace workspace)
    {
        var status = DevManifest.Str(envelope?["status"]);
        var target = workspace.PoolFilesFor(step);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);

        switch (status)
        {
            case PoolEntry.Succeeded:
                Steps[step] = new PoolEntry(PoolEntry.Succeeded, envelope!["output"]?.DeepClone(), null);
                if (Directory.Exists(workspace.WorkFiles))
                    CopyDirectory(workspace.WorkFiles, target);
                break;
            case PoolEntry.Skipped:
                Steps[step] = new PoolEntry(PoolEntry.Skipped, null, DevManifest.Str(envelope!["reason"]) ?? "skipped by the handler");
                break;
            default:
                Steps[step] = new PoolEntry(PoolEntry.Failed, null, DevManifest.Str(envelope?["error"]?["message"]) ?? "no result");
                break;
        }
    }

    /// <summary>Records a step skipped by pl itself (a <c>when</c> that did not match, or a skipped dependency).</summary>
    public void RecordSkipped(string step, string reason, DevWorkspace workspace)
    {
        var target = workspace.PoolFilesFor(step);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Steps[step] = new PoolEntry(PoolEntry.Skipped, null, reason);
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}

/// <summary>
/// The local workspace, <c>&lt;pipeline dir&gt;/.olve/</c> (already git-ignored there, and the dev
/// runner's own default <c>$OLVE_DIR</c>):
/// <code>
/// input.json   pool.json   work/output.json   work/files/   tmp/
/// files/0/&lt;step&gt;/1/files/   (pool files, OLVE_FILES_DIR)
/// build/       (olve-build output, --docker)   lib/&lt;tag&gt;/ (loader libs)   docker/ (staging)
/// </code>
/// </summary>
public sealed class DevWorkspace(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string InputJson => Path.Combine(Root, "input.json");
    public string PoolJson => Path.Combine(Root, "pool.json");
    public string Work => Path.Combine(Root, "work");
    public string WorkFiles => Path.Combine(Work, "files");
    public string OutputJson => Path.Combine(Work, "output.json");
    public string Tmp => Path.Combine(Root, "tmp");
    public string PoolFiles => Path.Combine(Root, "files");
    public string Build => Path.Combine(Root, "build");
    public string DockerStaging => Path.Combine(Root, "docker");

    public string LibCache(string tag) => Path.Combine(Root, "lib", tag);

    public string PoolFilesFor(string step) =>
        Path.Combine(PoolFiles, DevPool.Run.ToString(), step, DevPool.Attempt.ToString(), "files");
}
