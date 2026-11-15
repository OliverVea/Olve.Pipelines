using System.Text.Json.Serialization;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// The slice of a GitHub pull request the service reads, as the REST API (<c>GET …/pulls</c>, <c>…/pulls/{n}</c>) and
/// the <c>pull_request</c> webhook carry it. Listed pull requests have no <c>mergeable</c>; GitHub computes it lazily.
/// </summary>
public record GitHubPullRequest(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("merged_at")] DateTimeOffset? MergedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("head")] GitHubPullRequestRef Head,
    [property: JsonPropertyName("base")] GitHubPullRequestRef Base,
    [property: JsonPropertyName("mergeable")] bool? Mergeable = null,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha = null)
{
    /// <summary>From another repository; a fork whose repository was deleted has none.</summary>
    public bool FromFork => !string.Equals(Head.Repo?.FullName, Base.Repo?.FullName, StringComparison.OrdinalIgnoreCase);
}

public record GitHubPullRequestRef(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("repo")] GitHubRepositoryName? Repo);

public record GitHubRepositoryName([property: JsonPropertyName("full_name")] string FullName);

/// <summary>A git commit as <c>GET …/git/commits/{sha}</c> reads it: its tree and parents.</summary>
public record GitHubGitCommit(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("tree")] GitHubShaRef Tree,
    [property: JsonPropertyName("parents")] GitHubShaRef[] Parents);

public record GitHubShaRef([property: JsonPropertyName("sha")] string Sha);

/// <summary>The files a comparison (<c>GET …/compare/{base}...{head}</c>) changes; GitHub lists at most 300.</summary>
public record GitHubComparedFiles([property: JsonPropertyName("files")] GitHubComparedFile[]? Files);

public record GitHubComparedFile([property: JsonPropertyName("filename")] string Filename);

/// <summary>A <c>pull_request</c> webhook delivery.</summary>
public record GitHubPullRequestEvent(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("pull_request")] GitHubPullRequest? PullRequest);

/// <summary>The body of <c>POST …/statuses/{sha}</c>; a null <c>target_url</c> is left out.</summary>
public record GitHubCommitStatusRequest(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("target_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetUrl);

[JsonSerializable(typeof(GitHubPullRequest))]
[JsonSerializable(typeof(GitHubPullRequest[]))]
[JsonSerializable(typeof(GitHubPullRequestEvent))]
[JsonSerializable(typeof(GitHubGitCommit))]
[JsonSerializable(typeof(GitHubComparedFiles))]
[JsonSerializable(typeof(GitHubCommitStatusRequest))]
internal partial class GitHubPullRequestJsonContext : JsonSerializerContext;
