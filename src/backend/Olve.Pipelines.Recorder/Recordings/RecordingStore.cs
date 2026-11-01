namespace Olve.Pipelines.Recorder.Recordings;

/// <summary>The recordings under <c>tools/recordings/&lt;system&gt;/</c>, one JSON file per exchange.</summary>
public static class RecordingStore
{
    public const string Directory = "tools/recordings";

    /// <param name="Unreadable">Files that hold no valid recording, relative to the store.</param>
    public sealed record Contents(IReadOnlyList<Recording> Recordings, IReadOnlyList<string> Unreadable);

    /// <summary>The checkout <paramref name="start"/> sits in.</summary>
    public static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Olve.Pipelines.slnx")))
                return dir.FullName;
        }
        return null;
    }

    public static Contents Load(string repoRoot)
    {
        var store = Path.Combine(repoRoot, Directory);
        var recordings = new List<Recording>();
        var unreadable = new List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(store, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(store, file).Replace('\\', '/');
            if (!relative.Contains('/'))
                continue;
            try
            {
                recordings.Add(Recording.Parse(File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or System.Text.Json.JsonException)
            {
                unreadable.Add(relative);
            }
        }
        return new Contents(recordings, unreadable);
    }

    public static Recording Get(string repoRoot, string system, string name) =>
        Recording.Parse(File.ReadAllText(Path.Combine(repoRoot, Directory, system, $"{name}.json")));

    /// <summary>Replaces everything recorded for <paramref name="system"/>.</summary>
    public static void Replace(string repoRoot, string system, IEnumerable<Recording> recordings)
    {
        var dir = Path.Combine(repoRoot, Directory, system);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, recursive: true);
        System.IO.Directory.CreateDirectory(dir);
        foreach (var recording in recordings)
            File.WriteAllText(Path.Combine(dir, $"{recording.Name}.json"), recording.ToJson());
    }
}
