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
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha = null,
    [property: JsonPropertyName("auto_merge")] GitHubAutoMerge? AutoMerge = null)
{
    /// <summary>From another repository; a fork whose repository was deleted has none.</summary>
    public bool FromFork => !string.Equals(Head.Repo?.FullName, Base.Repo?.FullName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Set while GitHub auto-merge is turned on for the pull request.</summary>
public record GitHubAutoMerge([property: JsonPropertyName("merge_method")] string? MergeMethod);

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

/// <summary>A git reference (<c>GET …/git/ref/heads/{branch}</c>): the commit it points at.</summary>
public record GitHubGitRef(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("object")] GitHubShaRef Object);

/// <summary>The body of <c>POST …/git/refs</c>.</summary>
public record GitHubCreateRefRequest(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("sha")] string Sha);

/// <summary>The body of <c>POST …/merges</c>: merges <c>head</c> into the branch <c>base</c>.</summary>
public record GitHubMergeRequest(
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("commit_message")] string CommitMessage);

/// <summary>The merge commit <c>POST …/merges</c> made.</summary>
public record GitHubMergeCommit(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("commit")] GitHubMergeCommitDetail Commit);

public record GitHubMergeCommitDetail([property: JsonPropertyName("tree")] GitHubShaRef Tree);

/// <summary>The body of <c>PUT …/pulls/{n}/merge</c>: <c>sha</c> is the head that must still be the pull request's.</summary>
public record GitHubMergePullRequest(
    [property: JsonPropertyName("merge_method")] string MergeMethod,
    [property: JsonPropertyName("sha")] string Sha);

/// <summary>GitHub's answer to a write it refused: <c>message</c> says why.</summary>
public record GitHubMessage([property: JsonPropertyName("message")] string? Message);

[JsonSerializable(typeof(GitHubPullRequest))]
[JsonSerializable(typeof(GitHubPullRequest[]))]
[JsonSerializable(typeof(GitHubPullRequestEvent))]
[JsonSerializable(typeof(GitHubGitCommit))]
[JsonSerializable(typeof(GitHubComparedFiles))]
[JsonSerializable(typeof(GitHubCommitStatusRequest))]
[JsonSerializable(typeof(GitHubGitRef))]
[JsonSerializable(typeof(GitHubCreateRefRequest))]
[JsonSerializable(typeof(GitHubMergeRequest))]
[JsonSerializable(typeof(GitHubMergeCommit))]
[JsonSerializable(typeof(GitHubMergePullRequest))]
[JsonSerializable(typeof(GitHubMessage))]
internal partial class GitHubPullRequestJsonContext : JsonSerializerContext;
