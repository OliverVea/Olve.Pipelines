using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olve.Pipelines.RealSystemTests.Hosting;

public sealed record KubernetesTarget(string Server, string Token, string CaCertificate, string Namespace);

public sealed record StorageTarget(string Endpoint, string Bucket, string AccessKey, string SecretKey);

/// <summary>One host, as <c>tools/real-system/host.sh up</c> describes it.</summary>
public sealed record TestTarget(
    string ApiUrl,
    string ClusterId,
    KubernetesTarget Kubernetes,
    StorageTarget Storage,
    string GitHubUrl,
    string? ResetCommand,
    string? FaultCommand)
{
    public override string ToString() => ApiUrl;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TestTarget))]
internal partial class TestTargetJsonContext : JsonSerializerContext;

/// <summary>
/// Which hosts to use: <c>http://localhost:5000</c> with no configuration, the URLs in <c>TEST_TARGETS</c>
/// (comma separated) or the file <c>TEST_TARGETS_FILE</c> (one URL per line).
/// </summary>
public static class TestTargets
{
    public const string DefaultUrl = "http://localhost:5000";

    public static IReadOnlyList<string> ListUrls(Func<string, string?> environment, Func<string, string> readFile)
    {
        var listed = environment("TEST_TARGETS");
        if (!string.IsNullOrWhiteSpace(listed))
            return Split(listed);
        var file = environment("TEST_TARGETS_FILE");
        return !string.IsNullOrWhiteSpace(file) ? Split(readFile(file)) : [DefaultUrl];
    }

    public static string DescriptorName(string url)
    {
        var uri = new Uri(url);
        return $"{uri.Host}-{uri.Port}.json";
    }

    public static IReadOnlyList<TestTarget> Load()
    {
        var urls = ListUrls(Environment.GetEnvironmentVariable, File.ReadAllText);
        var directory = Path.Combine(RepoRoot(), "artifacts", "real-system");
        return urls.Select(url =>
        {
            var path = Path.Combine(directory, DescriptorName(url));
            if (!File.Exists(path))
                throw new InvalidOperationException($"No host at {url}: {path} does not exist. Start one with tools/real-system/host.sh up.");
            return JsonSerializer.Deserialize(File.ReadAllText(path), TestTargetJsonContext.Default.TestTarget)
                   ?? throw new InvalidOperationException($"{path} is empty.");
        }).ToList();
    }

    private static string[] Split(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Olve.Pipelines.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"No Olve.Pipelines.slnx above {AppContext.BaseDirectory}.");
    }
}
