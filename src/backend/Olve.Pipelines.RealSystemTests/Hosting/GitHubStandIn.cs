using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>The host's GitHub. Every repo already exists and holds the fixture pipeline.</summary>
public sealed class GitHubStandIn(HttpClient http)
{
    /// <summary>Lands a commit that changes <paramref name="files"/> (repo-relative path → content) and returns its sha.</summary>
    public async Task<string> CommitAsync(string repo, IReadOnlyDictionary<string, string> files)
    {
        var body = new JsonObject { ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create<string, JsonNode?>(f.Key, f.Value))) };
        using var response = await http.PostAsJsonAsync($"_test/repos/{repo}/commit", body);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<JsonObject>();
        return reply!["sha"]!.GetValue<string>();
    }
}
