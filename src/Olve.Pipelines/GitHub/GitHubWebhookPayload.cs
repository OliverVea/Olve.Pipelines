using System.Text.Json.Serialization;

namespace Olve.Pipelines.GitHub;

/// <summary>The slice of a GitHub <c>push</c> event payload we care about: the pushed ref.</summary>
public record GitHubPushPayload(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("after")] string? After = null,
    [property: JsonPropertyName("repository")] GitHubPushRepository? Repository = null);

/// <param name="PushedAt">Unix seconds of the push.</param>
public record GitHubPushRepository([property: JsonPropertyName("pushed_at")] long? PushedAt = null);

[JsonSerializable(typeof(GitHubPushPayload))]
internal partial class GitHubWebhookJsonContext : JsonSerializerContext;
