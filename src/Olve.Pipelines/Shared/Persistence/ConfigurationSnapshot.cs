using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;

namespace Olve.Pipelines.Shared.Persistence;

// v1 snapshots also carry productionSteps/processingSteps/triggers; those properties are ignored on
// load (unknown members are skipped) and dropped on the next save.
public record ConfigurationSnapshot(
    PipelineData[] Pipelines,
    PipelineConfigBindingData[]? Bindings = null);

public record PipelineData(Id<Pipeline> Id, string Name);

public record PipelineConfigBindingData(
    Id<PipelineConfigBinding> Id,
    Id<Pipeline> PipelineId,
    string Repo,
    string Branch,
    string Path,
    string? CredentialsSecret,
    string? LastDeployedSha,
    string? LastSyncedSha,
    ReconcileStatus? Status,
    DateTimeOffset CreatedAt,
    // Nullable for back-compat: a snapshot written before deploy-trigger modes existed has no value,
    // and such bindings load as Poll (their existing behavior) rather than silently adopting webhooks.
    BindingDeployTrigger? DeployTrigger = null,
    string? WebhookSecret = null);
