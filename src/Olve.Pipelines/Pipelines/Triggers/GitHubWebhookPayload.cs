using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>The slice of a GitHub <c>push</c> event payload we care about: the pushed ref.</summary>
public record GitHubPushPayload(
    [property: JsonPropertyName("ref")] string? Ref);

[JsonSerializable(typeof(GitHubPushPayload))]
internal partial class GitHubWebhookJsonContext : JsonSerializerContext;
