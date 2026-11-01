using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.Recorder.Capture;

/// <summary>
/// Sends what GitHubConfigSource sends, to a public repo. It reads no token from anywhere: every request
/// is anonymous, so nothing but public data can come back.
/// </summary>
internal static partial class GitHubCapture
{
    private const string Api = "https://api.github.com";
    private const string Repo = "OliverVea/Olve.Pipelines";
    private const string Branch = "main";
    private const string Path = ".pipelines";

    private static readonly string[] Headers = ["content-type", "etag", "x-github-api-version-selected", "x-github-media-type"];

    /// <summary>Only what the service reads stays, with no author details and no diffs of the repo.</summary>
    private static readonly string[] CommitElisions =
    [
        "author", "comments_url", "commit.author", "commit.comment_count", "commit.committer.email", "commit.committer.name",
        "commit.message", "commit.tree", "commit.url", "commit.verification", "committer", "files", "html_url", "node_id",
        "parents", "stats", "url",
    ];

    public static async Task<IReadOnlyList<Recording>> CaptureAsync(DateOnly today)
    {
        using var client = new HttpClient();
        var exchanges = new List<(string Name, HttpRequestMessage Request, HttpResponseMessage Response, string Text, string[] Elided)>();

        async Task<(HttpResponseMessage Response, string Text)> GetAsync(
            string name, string url, string? ifNoneMatch = null, string accept = "application/vnd.github+json", string[]? elided = null, bool userAgent = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Api + url);
            request.Headers.Accept.ParseAdd(accept);
            if (userAgent)
                request.Headers.UserAgent.ParseAdd("olve-pipelines");
            if (ifNoneMatch is not null)
                request.Headers.IfNoneMatch.ParseAdd(ifNoneMatch);
            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            exchanges.Add((name, request, response, text, elided ?? []));
            return (response, text);
        }

        var head = $"/repos/{Repo}/commits/{Branch}";
        await GetAsync("branch-head", head, elided: CommitElisions);
        await GetAsync("no-user-agent", head, userAgent: false);

        var (cursor, cursorText) = await CursorAsync("config-cursor", Path);
        var sha = JsonNode.Parse(cursorText)![0]!["sha"]!.GetValue<string>();
        var (_, packageJson) = await GetAsync(
            "package-json", $"/repos/{Repo}/contents/{Path}/package.json?ref={sha}", accept: "application/vnd.github.raw+json");
        var dependency = LocalDependency().Match(packageJson).Groups[1].Value;
        if (dependency.Length == 0)
            throw new InvalidOperationException($"{Path}/package.json has no file:../ dependency to record");
        await CursorAsync("dependency-cursor", dependency);

        var system = exchanges
            .SelectMany(e => e.Response.Headers)
            .FirstOrDefault(h => h.Key.Equals("x-github-api-version-selected", StringComparison.OrdinalIgnoreCase))
            .Value?.Single() ?? throw new InvalidOperationException("GitHub reported no API version");

        return exchanges.Select(e => ToRecording(e.Name, e.Request, e.Response, e.Text, e.Elided, system, today)).ToList();

        async Task<(HttpResponseMessage, string)> CursorAsync(string name, string path)
        {
            var url = $"/repos/{Repo}/commits?path={Uri.EscapeDataString(path)}&sha={Branch}&per_page=1";
            var first = await GetAsync(name, url, elided: CommitElisions);
            await GetAsync($"{name}-not-modified", url, ifNoneMatch: first.Response.Headers.ETag!.ToString());
            return first;
        }
    }

    private static Recording ToRecording(
        string name, HttpRequestMessage request, HttpResponseMessage response, string text, string[] elided, string systemVersion, DateOnly today)
    {
        var requestHeaders = Scrubber.KeepHeaders(request.Headers.Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value))), "Accept", "If-None-Match", "User-Agent");
        var responseHeaders = Scrubber.KeepHeaders(
            response.Headers.Concat(response.Content.Headers).Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value))), Headers);
        var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true
            && !request.Headers.Accept.ToString().Contains("raw", StringComparison.Ordinal);
        var body = text.Length == 0
            ? null
            : isJson ? Scrubber.Scrub(Scrubber.Elide(JsonNode.Parse(text), elided)) : JsonValue.Create(Scrubber.Scrub(text));
        return new Recording(
            SystemPins.GitHub, name, $"{Api} (anonymous)", today, systemVersion, elided,
            new RecordedRequest("GET", request.RequestUri!.PathAndQuery, requestHeaders),
            new RecordedResponse((int)response.StatusCode, responseHeaders, body));
    }

    [GeneratedRegex(@"""(?:file|link):\.\./([\w./-]+?)/?""")]
    private static partial Regex LocalDependency();
}
