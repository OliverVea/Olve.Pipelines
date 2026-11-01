using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.Recorder.Capture;

/// <summary>A throwaway Dex on the pinned image and the repo's local config.</summary>
internal static partial class DexCapture
{
    public static async Task<IReadOnlyList<Recording>> CaptureAsync(string repoRoot, DateOnly today)
    {
        var version = SystemPins.Single(repoRoot, SystemPins.Dex);
        var image = $"dexidp/dex:{version}";

        var reported = ReportedVersion().Match(Captures.Run("docker", ["run", "--rm", image, "dex", "version"])).Value;
        if (reported != version)
            throw new InvalidOperationException($"{image} reports version '{reported}'");

        var container = $"olve-recorder-dex-{Guid.NewGuid():N}";
        var config = Path.Combine(repoRoot, "src/deploy/compose/dex.yaml");
        Captures.Run("docker", ["run", "-d", "--name", container, "-p", "127.0.0.1::5556", "-v", $"{config}:/etc/dex/config.yaml:ro", image, "dex", "serve", "/etc/dex/config.yaml"]);
        try
        {
            var address = Captures.Run("docker", ["port", container, "5556/tcp"]).Trim();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{address}") };
            await WaitForAsync(client);
            var source = $"{image} with src/deploy/compose/dex.yaml";
            return
            [
                await RecordAsync(client, "discovery", "/dex/.well-known/openid-configuration", source, version, today),
                await RecordAsync(client, "keys", "/dex/keys", source, version, today),
            ];
        }
        finally
        {
            Captures.Run("docker", ["rm", "-f", container]);
        }
    }

    private static async Task WaitForAsync(HttpClient client)
    {
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var response = await client.GetAsync("/dex/healthz");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Dex did not become ready");
    }

    private static async Task<Recording> RecordAsync(HttpClient client, string name, string url, string source, string version, DateOnly today)
    {
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var headers = Scrubber.KeepHeaders(response.Content.Headers.Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value))), "content-type");
        return new Recording(
            SystemPins.Dex, name, source, today, version, [],
            new RecordedRequest("GET", url, new Dictionary<string, string>()),
            new RecordedResponse((int)response.StatusCode, headers, Scrubber.Scrub(JsonNode.Parse(await response.Content.ReadAsStringAsync()))));
    }

    [GeneratedRegex(@"v\d+\.\d+\.\d+")]
    private static partial Regex ReportedVersion();
}
