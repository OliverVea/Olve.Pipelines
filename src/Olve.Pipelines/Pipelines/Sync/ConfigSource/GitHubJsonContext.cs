using System.Text.Json.Serialization;

namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

internal record GitHubCommitRef(string Sha);

internal record GitHubCommitListItem(string Sha);

internal record GitHubTree(GitHubTreeEntry[] Tree, bool Truncated);

internal record GitHubTreeEntry(string Path, string Type, string Sha);

internal record GitHubBlob(string Content, string Encoding);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitHubCommitRef))]
[JsonSerializable(typeof(GitHubCommitListItem[]))]
[JsonSerializable(typeof(GitHubTree))]
[JsonSerializable(typeof(GitHubBlob))]
internal partial class GitHubJsonContext : JsonSerializerContext;
