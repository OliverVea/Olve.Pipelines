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

    /// <summary>All of a pull request the service reads; everything else (bodies, users, links, ids) is dropped.</summary>
    private static readonly string[] PullFields =
    [
        "number", "title", "state", "draft", "merged_at", "updated_at", "mergeable",
        "head.ref", "head.sha", "head.repo.full_name", "base.ref", "base.repo.full_name",
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

        // What the pull-request catch-up asks for, and pull requests as GitHub lists and reads them (closed ones: a
        // public repo has no open pull request to rely on).
        await GetAsync("pulls-open", $"/repos/{Repo}/pulls?state=open&base={Branch}&per_page=100&page=1", elided: PullElisions);
        var (_, closedText) = await GetAsync("pulls-listed", $"/repos/{Repo}/pulls?state=closed&base={Branch}&per_page=2&page=1", elided: PullElisions);
        var pullNumber = JsonNode.Parse(closedText)![0]!["number"]!.GetValue<int>();
        await GetAsync("pull", $"/repos/{Repo}/pulls/{pullNumber}", elided: PullElisions);
        await GetAsync("pull-unknown", $"/repos/{Repo}/pulls/999999");

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

    /// <summary>Every field of a pull request (listed or read) outside <see cref="PullFields"/>.</summary>
    private static readonly string[] PullElisions =
    [
        "_links", "active_lock_reason", "additions", "assignee", "assignees", "author_association", "auto_merge", "body",
        "changed_files", "closed_at", "comments", "comments_url", "commits", "commits_url", "created_at", "deletions", "diff_url",
        "html_url", "id", "issue_url", "labels", "locked", "maintainer_can_modify", "merge_commit_sha", "mergeable_state", "merged",
        "merged_by", "milestone", "node_id", "patch_url", "rebaseable", "requested_reviewers", "requested_teams",
        "review_comment_url", "review_comments", "review_comments_url", "statuses_url", "url", "user",
        "head.label", "head.user", "base.label", "base.user",
        .. RepoElisions("head"), .. RepoElisions("base"),
    ];

    private static IEnumerable<string> RepoElisions(string side) =>
    [
        $"{side}.repo.allow_forking", $"{side}.repo.archive_url", $"{side}.repo.archived", $"{side}.repo.assignees_url",
        $"{side}.repo.blobs_url", $"{side}.repo.branches_url", $"{side}.repo.clone_url", $"{side}.repo.collaborators_url",
        $"{side}.repo.comments_url", $"{side}.repo.commits_url", $"{side}.repo.compare_url", $"{side}.repo.contents_url",
        $"{side}.repo.contributors_url", $"{side}.repo.created_at", $"{side}.repo.default_branch", $"{side}.repo.deployments_url",
        $"{side}.repo.description", $"{side}.repo.disabled", $"{side}.repo.downloads_url", $"{side}.repo.events_url", $"{side}.repo.fork",
        $"{side}.repo.forks", $"{side}.repo.forks_count", $"{side}.repo.forks_url", $"{side}.repo.git_commits_url",
        $"{side}.repo.git_refs_url", $"{side}.repo.git_tags_url", $"{side}.repo.git_url", $"{side}.repo.has_discussions",
        $"{side}.repo.has_downloads", $"{side}.repo.has_issues", $"{side}.repo.has_pages", $"{side}.repo.has_projects",
        $"{side}.repo.has_pull_requests", $"{side}.repo.has_wiki", $"{side}.repo.homepage", $"{side}.repo.hooks_url",
        $"{side}.repo.html_url", $"{side}.repo.id", $"{side}.repo.is_template", $"{side}.repo.issue_comment_url",
        $"{side}.repo.issue_events_url", $"{side}.repo.issues_url", $"{side}.repo.keys_url", $"{side}.repo.labels_url",
        $"{side}.repo.language", $"{side}.repo.languages_url", $"{side}.repo.license", $"{side}.repo.merges_url",
        $"{side}.repo.milestones_url", $"{side}.repo.mirror_url", $"{side}.repo.name", $"{side}.repo.node_id",
        $"{side}.repo.notifications_url", $"{side}.repo.open_issues", $"{side}.repo.open_issues_count", $"{side}.repo.owner",
        $"{side}.repo.private", $"{side}.repo.pull_request_creation_policy", $"{side}.repo.pulls_url", $"{side}.repo.pushed_at", $"{side}.repo.releases_url",
        $"{side}.repo.size", $"{side}.repo.ssh_url", $"{side}.repo.stargazers_count", $"{side}.repo.stargazers_url",
        $"{side}.repo.statuses_url", $"{side}.repo.subscribers_url", $"{side}.repo.subscription_url", $"{side}.repo.svn_url",
        $"{side}.repo.tags_url", $"{side}.repo.teams_url", $"{side}.repo.topics", $"{side}.repo.trees_url", $"{side}.repo.updated_at",
        $"{side}.repo.url", $"{side}.repo.visibility", $"{side}.repo.watchers", $"{side}.repo.watchers_count", $"{side}.repo.web_commit_signoff_required",
    ];

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
