using System.Net;
using System.Text.Json.Nodes;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.RealSystemTests.Flows;

/// <summary>The stand-in is an assumption about GitHub, so its replies are held against what GitHub sent.</summary>
public class GitHubStandInTests
{
    private static readonly string Root = RecordingStore.FindRepoRoot(AppContext.BaseDirectory)!;

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url.TrimStart('/'));

    private static void Describe(JsonNode? node, string path, ISet<string> shape)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var (key, value) in item)
                    Describe(value, $"{path}.{key}", shape);
                break;
            case JsonArray list:
                foreach (var value in list)
                    Describe(value, $"{path}[]", shape);
                break;
            default:
                shape.Add($"{path}: {node?.GetValueKind()}");
                break;
        }
    }

    private static HashSet<string> Shape(JsonNode? node)
    {
        var shape = new HashSet<string>();
        Describe(node, "$", shape);
        return shape;
    }

    [Test]
    [Arguments("branch-head", "commits/main")]
    [Arguments("config-cursor", "commits?path=.pipelines&sha=main&per_page=1")]
    public async Task EveryFieldGitHubSends_IsInTheStandInsReply(string name, string url)
    {
        await using var server = await TestServer.LeaseAsync();
        var recording = RecordingStore.Get(Root, SystemPins.GitHub, name);
        var repo = $"e2e/{TestServer.NewName("stand-in")}";

        using var response = await server.GitHub.SendAsync(Get($"repos/{repo}/{url}"));

        await Assert.That((int)response.StatusCode).IsEqualTo(recording.Response.Status);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(Shape(reply)).IsSupersetOf(Shape(recording.Response.Body));
    }

    [Test]
    public async Task AnUnchangedCursor_IsAnEmpty304_AsOnGitHub()
    {
        await using var server = await TestServer.LeaseAsync();
        var recording = RecordingStore.Get(Root, SystemPins.GitHub, "config-cursor-not-modified");
        var url = $"repos/e2e/{TestServer.NewName("stand-in")}/commits?per_page=1";
        using var first = await server.GitHub.SendAsync(Get(url));
        using var repeat = Get(url);
        repeat.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var response = await server.GitHub.SendAsync(repeat);

        await Assert.That((int)response.StatusCode).IsEqualTo(recording.Response.Status);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(recording.Response.BodyText);
    }

    [Test]
    public async Task AFileThatIsNotThere_Is404()
    {
        await using var server = await TestServer.LeaseAsync();

        using var response = await server.GitHub.SendAsync(Get($"repos/e2e/{TestServer.NewName("stand-in")}/contents/missing.json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
