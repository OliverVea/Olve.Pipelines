using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Triggers;

public record Trigger(
    Id<Trigger> Id,
    Id<Pipeline> PipelineId,
    string Name,
    TriggerTarget Target,
    string Secret,
    DateTimeOffset CreatedAt) : IHasId<Id<Trigger>>;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProductionTriggerTarget), "production")]
[JsonDerivedType(typeof(ProcessingTriggerTarget), "processing")]
[JsonDerivedType(typeof(PollTriggerTarget), "poll")]
[JsonDerivedType(typeof(GitHubWebhookTarget), "github")]
public abstract record TriggerTarget;

public record ProductionTriggerTarget : TriggerTarget;

public record ProcessingTriggerTarget(Id<ProcessingStep> ProcessingStepId) : TriggerTarget;

public record PollTriggerTarget(
    string Url,
    Dictionary<string, string>? Headers,
    string ValuePath,
    int IntervalSeconds = 60) : TriggerTarget;

/// <summary>
/// Fires production from a GitHub <c>push</c> webhook. Olve.Pipelines auto-registers/deregisters
/// the hook on the repo via the GitHub API (see GitHub auto-registration). The trigger's own
/// <see cref="Trigger.Secret"/> doubles as the webhook HMAC key. <paramref name="TokenSecretName"/>
/// names the key in the pipeline's K8s secret holding a fine-grained PAT (scopes: repo +
/// admin:repo_hook) used to manage the hook.
/// </summary>
public record GitHubWebhookTarget(
    string Owner,
    string Repo,
    string Branch,
    string TokenSecretName) : TriggerTarget;
