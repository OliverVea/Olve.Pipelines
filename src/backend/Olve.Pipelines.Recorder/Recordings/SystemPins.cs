using System.Text.RegularExpressions;

namespace Olve.Pipelines.Recorder.Recordings;

/// <summary>A version the repo pins a recorded system to, and the file that pins it.</summary>
public sealed record SystemPin(string System, string Version, string File);

/// <summary>
/// Versions come from the repo only, never from a running cluster. The homelab's k3s isn't pinned here,
/// so what tracks k3s is the image the throwaway hosts run.
/// </summary>
public static partial class SystemPins
{
    public const string Dex = "dex";
    public const string K3s = "k3s";
    public const string GitHub = "github";

    private static readonly (string System, Regex Image, string File)[] Sources =
    [
        (Dex, DexImage(), "docker-compose.yml"),
        (Dex, DexImage(), "src/deploy/helm/values.yaml"),
        (K3s, K3sImage(), "docker-compose.yml"),
        (K3s, K3sImage(), "tools/real-system/compose.yml"),
    ];

    public static IReadOnlyList<SystemPin> Read(string repoRoot) =>
        Sources
            .SelectMany(s => s.Image
                .Matches(Text(repoRoot, s.File))
                .Select(m => new SystemPin(s.System, m.Groups[1].Value, s.File)))
            .Distinct()
            .ToList();

    /// <summary>The one version to record: a capture can't be true of pins that disagree.</summary>
    public static string Single(string repoRoot, string system)
    {
        var versions = Read(repoRoot).Where(p => p.System == system).Select(p => p.Version).Distinct().ToList();
        return versions.Count == 1
            ? versions[0]
            : throw new InvalidOperationException($"expected one {system} pin, found: {string.Join(", ", versions)}");
    }

    private static string Text(string repoRoot, string file)
    {
        var path = Path.Combine(repoRoot, file);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    [GeneratedRegex(@"dexidp/dex:([\w.+-]+)")]
    private static partial Regex DexImage();

    [GeneratedRegex(@"rancher/k3s:([\w.+-]+)")]
    private static partial Regex K3sImage();
}
