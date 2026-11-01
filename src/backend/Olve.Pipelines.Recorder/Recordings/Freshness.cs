namespace Olve.Pipelines.Recorder.Recordings;

/// <summary>
/// How current the recordings are, as one line for the end of a test run. An out-of-date recording is a
/// warning: nothing here throws or fails a build, and tests that replay recordings never look at dates.
/// </summary>
public static class Freshness
{
    public const int GitHubExpiryDays = 60;
    public const string SummaryFile = "artifacts/recorded-fixtures.txt";

    private const string ReRecord = "re-record: dotnet run --project src/backend/Olve.Pipelines.Recorder -- <system>";

    public static string Summarize(RecordingStore.Contents contents, IReadOnlyList<SystemPin> pins, DateOnly today)
    {
        var recordings = contents.Recordings;

        var expired = recordings.Count(r => r.System == SystemPins.GitHub && today.DayNumber - r.CapturedAt.DayNumber > GitHubExpiryDays);

        var versioned = recordings.Where(r => r.System != SystemPins.GitHub).ToList();
        var unpinned = versioned.Select(r => r.System).Distinct().Where(s => pins.All(p => p.System != s)).Order(StringComparer.Ordinal).ToList();
        var stale = versioned
            .Where(r => !unpinned.Contains(r.System) && pins.Any(p => p.System == r.System && p.Version != r.SystemVersion))
            .ToList();

        var problems = new List<string>();
        if (expired > 0)
            problems.Add($"{expired} expired (github, over {GitHubExpiryDays} days)");
        if (stale.Count > 0)
            problems.Add($"{stale.Count} stale ({string.Join(", ", StaleSystems(stale, pins))})");
        if (contents.Unreadable.Count > 0)
            problems.Add($"{contents.Unreadable.Count} unreadable ({string.Join(", ", contents.Unreadable)})");
        if (unpinned.Count > 0)
            problems.Add($"no version pin found for {string.Join(", ", unpinned)}");

        var count = recordings.Count + contents.Unreadable.Count;
        return problems.Count == 0
            ? $"recorded fixtures: {count} current"
            : $"recorded fixtures: {count} total; {string.Join("; ", problems)}; {ReRecord}";
    }

    private static IEnumerable<string> StaleSystems(IReadOnlyList<Recording> stale, IReadOnlyList<SystemPin> pins) =>
        stale
            .GroupBy(r => r.System)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var recorded = string.Join(", ", g.Select(r => r.SystemVersion).Distinct().Order(StringComparer.Ordinal));
                var pinned = string.Join(", ", pins.Where(p => p.System == g.Key).Select(p => p.Version).Distinct().Order(StringComparer.Ordinal));
                return $"{g.Key}: recorded {recorded}, pinned {pinned}";
            });

    /// <summary>The line for the checkout around <paramref name="start"/>. Never throws.</summary>
    public static string Report(string start, TimeProvider clock)
    {
        try
        {
            if (RecordingStore.FindRepoRoot(start) is not { } root)
                return "recorded fixtures: not checked, no checkout found";
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            return Summarize(RecordingStore.Load(root), SystemPins.Read(root), today);
        }
        catch (Exception ex)
        {
            return $"recorded fixtures: not checked, {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
        }
    }

    /// <summary>
    /// Writes the line to <see cref="SummaryFile"/>, because <c>dotnet test</c> hides what a test process
    /// prints. Pipeline steps and <c>tools/check.sh</c> show the file. Never throws.
    /// </summary>
    public static void Publish(string start, string line)
    {
        try
        {
            if (RecordingStore.FindRepoRoot(start) is not { } root)
                return;
            var path = Path.Combine(root, SummaryFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
