using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// A pipeline's git connection (one per pipeline): the repo whose <see cref="Path"/> holds the pipeline
/// definition. <see cref="DeployPollService"/> reconciles the newest config commit first, then fires the
/// push trigger when the branch head moves. It always polls; a GitHub push webhook, registered when the
/// instance has a public URL and the binding has credentials, only makes that faster.
/// </summary>
/// <param name="CredentialsSecret">A key into the pipeline's K8s Secret holding a GitHub read token (never the token); null = public repo.</param>
/// <param name="LastPushSha">The branch head the push trigger last fired for.</param>
/// <param name="LastReconciledSha">The config commit (touching <see cref="Path"/> or a local <c>file:</c>/<c>link:</c> dependency of its
/// <c>package.json</c>) the last successful reconcile applied.</param>
/// <param name="WebhookSecret">HMAC key of the inbound push webhook (null on bindings from before webhooks; created when one is registered).</param>
public record PipelineConfigBinding(
    Id<PipelineConfigBinding> Id,
    Id<Pipeline> PipelineId,
    string Repo,
    string Branch,
    string Path,
    string? CredentialsSecret,
    string? LastPushSha,
    string? LastReconciledSha,
    ReconcileStatus Status,
    DateTimeOffset CreatedAt,
    string? WebhookSecret = null) : IHasId<Id<PipelineConfigBinding>>;
