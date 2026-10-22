using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.Shared.Persistence;

// v1 snapshots also carry productionSteps/processingSteps/triggers; those properties are ignored on
// load (unknown members are skipped) and dropped on the next save.
public record ConfigurationSnapshot(
    PipelineData[] Pipelines,
    PipelineConfigBindingData[]? Bindings = null);

public record PipelineData(Id<Pipeline> Id, string Name, string? Description = null);

public record PipelineConfigBindingData(
    Id<PipelineConfigBinding> Id,
    Id<Pipeline> PipelineId,
    string Repo,
    string Branch,
    string Path,
    string? CredentialsSecret,
    string? LastPushSha,
    string? LastReconciledSha,
    ReconcileStatus? Status,
    DateTimeOffset CreatedAt,
    string? WebhookSecret = null,
    // Pre-rename names of LastPushSha/LastReconciledSha: read from older snapshots, never written.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastDeployedSha = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastSyncedSha = null);
