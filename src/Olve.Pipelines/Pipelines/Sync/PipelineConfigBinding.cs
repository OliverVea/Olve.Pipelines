using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// How a bound pipeline learns it should deploy.
/// <list type="bullet">
/// <item><see cref="Webhook"/> — a GitHub push hook is auto-registered and drives reconcile+deploy;
/// the deploy poll still runs as a slow safety net (catches a missed/failed delivery). The default.</item>
/// <item><see cref="WebhookOnly"/> — webhook only; the deploy poll is suppressed once the hook is
/// live. The explicit "opt out of polling" mode.</item>
/// <item><see cref="Poll"/> — no webhook; the deploy poll is the sole trigger.</item>
/// </list>
/// When a webhook cannot be registered (no public base URL, a non-GitHub host, or a failed
/// registration) polling continues regardless of mode, so deploys never silently stop.
///
/// Serialized as an integer (0=Webhook, 1=WebhookOnly, 2=Poll), matching the other API enums; do
/// not reorder the members.
/// </summary>
public enum BindingDeployTrigger
{
    Webhook,
    WebhookOnly,
    Poll,
}

/// <summary>
/// A pipeline's git connection (one per pipeline): the repo whose <see cref="Path"/> holds the pipeline
/// definition. <see cref="DeployPollService"/> reconciles the newest config commit first, then fires the
/// push trigger when the branch head moves.
/// </summary>
/// <param name="CredentialsSecret">A key into the pipeline's K8s Secret holding a GitHub read token (never the token); null = public repo.</param>
/// <param name="LastPushSha">The branch head the push trigger last fired for.</param>
/// <param name="LastReconciledSha">The config commit (touching <see cref="Path"/> or a local <c>file:</c>/<c>link:</c> dependency of its
/// <c>package.json</c>) the last successful reconcile applied.</param>
/// <param name="WebhookSecret">HMAC key of the inbound push webhook; null until a webhook mode is selected.</param>
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
    BindingDeployTrigger DeployTrigger = BindingDeployTrigger.Webhook,
    string? WebhookSecret = null) : IHasId<Id<PipelineConfigBinding>>;
