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
    private const string UnknownSha = "0000000000000000000000000000000000000000";

    private static readonly string[] Headers = ["content-type", "etag", "x-github-api-version-selected", "x-github-media-type"];

    /// <summary>Only what the service reads stays, with no author details and no diffs of the repo; the head keeps its message.</summary>
    private static readonly string[] HeadElisions =
    [
        "author", "comments_url", "commit.author", "commit.comment_count", "commit.committer.email", "commit.committer.name",
        "commit.tree", "commit.url", "commit.verification", "committer", "files", "html_url", "node_id",
        "parents", "stats", "url",
    ];

    private static readonly string[] CommitElisions = [.. HeadElisions, "commit.message"];

    /// <summary>A comparison keeps each commit's sha, link, login, author name, committer date and message.</summary>
    private static readonly string[] CompareElisions =
    [
        "ahead_by", "base_commit", "behind_by", "diff_url", "files", "merge_base_commit", "patch_url", "permalink_url", "status", "url",
        "commits.author.avatar_url", "commits.author.events_url", "commits.author.followers_url", "commits.author.following_url",
        "commits.author.gists_url", "commits.author.gravatar_id", "commits.author.html_url", "commits.author.id", "commits.author.node_id",
        "commits.author.organizations_url", "commits.author.received_events_url", "commits.author.repos_url", "commits.author.site_admin",
        "commits.author.starred_url", "commits.author.subscriptions_url", "commits.author.type", "commits.author.url",
        "commits.author.user_view_type", "commits.comments_url", "commits.commit.author.email", "commits.commit.author.date",
        "commits.commit.comment_count", "commits.commit.committer.email", "commits.commit.committer.name", "commits.commit.tree",
        "commits.commit.url", "commits.commit.verification", "commits.committer", "commits.node_id", "commits.parents", "commits.url",
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
        var (_, headText) = await GetAsync("branch-head", head, elided: HeadElisions);
        var headSha = JsonNode.Parse(headText)!["sha"]!.GetValue<string>();
        var compareBase = await AncestorAsync(headSha, 3);
        await GetAsync("compare", $"/repos/{Repo}/compare/{compareBase}...{headSha}", elided: CompareElisions);
        await GetAsync("compare-unknown", $"/repos/{Repo}/compare/{UnknownSha}...{headSha}");
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

        // The first-parent ancestor `depth` commits back, read without recording it.
        async Task<string> AncestorAsync(string sha, int depth)
        {
            for (var i = 0; i < depth; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{Repo}/commits/{sha}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.UserAgent.ParseAdd("olve-pipelines");
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                sha = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["parents"]![0]!["sha"]!.GetValue<string>();
            }

            return sha;
        }

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
