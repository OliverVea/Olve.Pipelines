using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

internal record GitHubCommitRef(string Sha, GitHubCommitDetail? Commit);

internal record GitHubCommitListItem(string Sha, GitHubCommitDetail? Commit);

internal record GitHubCommitDetail(GitHubCommitPerson? Committer, string? Message = null);

internal record GitHubCommitPerson(DateTimeOffset? Date, string? Name = null);

internal record GitHubComparison(
    [property: JsonPropertyName("total_commits")] int TotalCommits,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    GitHubComparedCommit[] Commits);

internal record GitHubComparedCommit(
    string Sha,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    GitHubUser? Author,
    GitHubComparedDetail? Commit);

internal record GitHubComparedDetail(GitHubCommitPerson? Author, GitHubCommitPerson? Committer, string? Message);

internal record GitHubUser(string? Login);

internal record GitHubTree(GitHubTreeEntry[] Tree, bool Truncated);

internal record GitHubTreeEntry(string Path, string Type, string Sha);

internal record GitHubBlob(string Content, string Encoding);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitHubCommitRef))]
[JsonSerializable(typeof(GitHubCommitListItem[]))]
[JsonSerializable(typeof(GitHubTree))]
[JsonSerializable(typeof(GitHubBlob))]
[JsonSerializable(typeof(GitHubComparison))]
internal partial class GitHubJsonContext : JsonSerializerContext;
