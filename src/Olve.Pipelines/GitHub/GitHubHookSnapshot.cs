using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// Persisted GitHub hook registrations, kept separate from the config and promotion snapshots: it is
/// operational state (the live hook ids GitHub assigned), not git-owned config. Survives restart so
/// a trigger deleted after a restart can still have its hook removed from the repo.
/// </summary>
public record GitHubHookSnapshot(GitHubHookEntry[] Hooks);

public record GitHubHookEntry(
    Id<Trigger> TriggerId,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repo,
    long HookId,
    string TokenSecretName);

[JsonSerializable(typeof(GitHubHookSnapshot))]
internal partial class GitHubHookPersistenceJsonContext : JsonSerializerContext;
