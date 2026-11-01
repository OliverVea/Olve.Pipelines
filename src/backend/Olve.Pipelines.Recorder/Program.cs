using Olve.Pipelines.Recorder.Capture;
using Olve.Pipelines.Recorder.Recordings;

const string Usage = """
    usage: dotnet run --project src/backend/Olve.Pipelines.Recorder -- <command>

      github        record GitHub, anonymously, from a public repo
      dex           record a throwaway Dex on the pinned image (needs Docker)
      k3s [slot]    record a throwaway host; start it first with tools/real-system/host.sh up [slot]
      status        print how current the recordings are

    A recording replaces the system's files in tools/recordings and shows the diff. Nothing is staged:
    commit it to approve, or restore the directory to reject.
    """;

var root = RecordingStore.FindRepoRoot(Environment.CurrentDirectory);
if (args.Length == 0 || root is null)
{
    Console.Error.WriteLine(root is null ? "run this inside the repository" : Usage);
    return 64;
}

var system = args[0];
var today = DateOnly.FromDateTime(DateTime.UtcNow);
switch (system)
{
    case "status":
        Console.WriteLine(Freshness.Report(root, TimeProvider.System));
        return 0;
    case SystemPins.GitHub:
        return Finish(await GitHubCapture.CaptureAsync(today));
    case SystemPins.Dex:
        return Finish(await DexCapture.CaptureAsync(root, today));
    case SystemPins.K3s:
        return Finish(K3sCapture.Capture(root, args.Length > 1 ? int.Parse(args[1]) : 0, today));
    default:
        Console.Error.WriteLine(Usage);
        return 64;
}

int Finish(IReadOnlyList<Recording> recordings)
{
    var leaks = recordings.SelectMany(r => Scrubber.Leaks(r.ToJson()).Select(_ => r.Id)).Distinct().ToList();
    if (leaks.Count > 0)
    {
        Console.Error.WriteLine($"not written, something that looks like a secret survived scrubbing in: {string.Join(", ", leaks)}");
        return 1;
    }

    RecordingStore.Replace(root, system, recordings);
    var directory = $"{RecordingStore.Directory}/{system}";
    Console.WriteLine(Captures.Run("git", ["-c", "color.ui=never", "diff", "--", directory], root));
    foreach (var added in Captures.Run("git", ["ls-files", "--others", "--exclude-standard", directory], root).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        Console.WriteLine($"new: {added}\n{File.ReadAllText(Path.Combine(root, added))}");
    Console.WriteLine($"Review the diff. Approve: git add {directory} && git commit. Reject: git checkout -- {directory} && git clean -f {directory}");
    return 0;
}
