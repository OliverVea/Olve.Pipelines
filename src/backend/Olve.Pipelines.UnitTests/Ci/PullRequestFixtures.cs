using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.UnitTests.Recordings;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>Pull requests shaped as GitHub really sends them: the recorded <c>pull</c> exchange, varied field by field.</summary>
internal static class PullRequestFixtures
{
    public const string Repo = "OliverVea/Olve.Pipelines";

    /// <summary>The recorded pull request's JSON, with <paramref name="change"/> applied.</summary>
    public static JsonObject Json(Action<JsonObject>? change = null)
    {
        var pull = RecordingReplay.Get("github", "pull").Response.Body!.DeepClone().AsObject();
        change?.Invoke(pull);
        return pull;
    }

    public static GitHubPullRequest Pull(
        int number = 7, string @base = "main", string state = "open", bool merged = false, bool draft = false,
        string headSha = "aaaaaaa", string? headRepo = Repo, DateTimeOffset? updatedAt = null, bool? mergeable = null, string title = "Add a thing",
        string? mergeCommitSha = null, bool autoMerge = false) =>
        Parse(Json(p =>
        {
            p["number"] = number;
            p["title"] = title;
            p["state"] = state;
            p["draft"] = draft;
            p["merged_at"] = merged ? "2026-11-15T10:53:04Z" : null;
            p["updated_at"] = (updatedAt ?? DateTimeOffset.Parse("2026-11-15T10:00:00Z")).ToString("O");
            p["mergeable"] = mergeable;
            p["merge_commit_sha"] = mergeCommitSha;
            p["head"]!["sha"] = headSha;
            p["head"]!["ref"] = $"feature-{number}";
            p["head"]!["repo"] = headRepo is null ? null : new JsonObject { ["full_name"] = headRepo };
            p["base"]!["ref"] = @base;
            p["auto_merge"] = autoMerge ? new JsonObject { ["merge_method"] = "rebase" } : null;
        }));

    public static GitHubPullRequest Parse(JsonNode json) =>
        json.Deserialize(GitHubPullRequestJsonContext.Default.GitHubPullRequest)!;
}
