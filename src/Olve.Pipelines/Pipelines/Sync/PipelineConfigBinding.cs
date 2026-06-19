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
/// Binds a pipeline to its GitOps configuration source (a git repo). One binding per
/// pipeline; the repo is the sole source of truth for the pipeline's configuration.
///
/// The binding is the pipeline's git connection. From it the service runs a single
/// poll of the bound repo's branch head that is sequenced
/// <b>config-before-build</b>: on a head advance it first reconciles configuration (only
/// when the <c>.pipelines/</c> subtree changed) and then enqueues a production build for
/// the new commit. Config-apply gates the build. <see cref="DeployTrigger"/> selects whether
/// that cycle is driven by an auto-registered push webhook, polling, or both; the webhook path
/// runs the same reconcile-before-build cycle, authenticated by <see cref="WebhookSecret"/>.
///
/// <para>
/// Source fields:
/// <list type="bullet">
/// <item><see cref="Repo"/> — <c>owner/name</c> of the GitHub repository.</item>
/// <item><see cref="Branch"/> — branch whose head the deploy poll watches and whose
/// <see cref="Path"/> subtree the config poll reconciles.</item>
/// <item><see cref="Path"/> — path to the config directory (e.g. <c>.pipelines</c>).</item>
/// <item><see cref="CredentialsSecret"/> — a <b>reference</b> (key name) into the
/// pipeline's own k8s secret <c>olve-pipeline-{id:N}</c> holding the GitHub read token.
/// Never a raw token — raw values must not reach the S3 snapshot. <c>null</c> for a public
/// repo needing no auth.</item>
/// <item><see cref="LastDeployedSha"/> — the deploy cursor; the branch-head SHA the last
/// production build ran for. <c>null</c> until the first build.</item>
/// <item><see cref="LastSyncedSha"/> — the config cursor; the <c>.pipelines/</c> commit SHA the
/// last successful reconcile applied. Advances only on a fully successful apply. <c>null</c> until
/// the first reconcile.</item>
/// <item><see cref="DeployTrigger"/> — webhook (default), webhook-only, or poll. Mutable after
/// binding. See <see cref="BindingDeployTrigger"/>.</item>
/// <item><see cref="WebhookSecret"/> — the HMAC key for the inbound binding webhook (generated,
/// not git-owned). <c>null</c> until a webhook mode is selected.</item>
/// </list>
/// </para>
/// <item><see cref="Status"/> — the outcome of the most recent reconcile attempt (result,
/// problems, declared secrets), surfaced via the status endpoint and frontend badge.</item>
/// </list>
/// </para>
/// </summary>
public record PipelineConfigBinding(
    Id<PipelineConfigBinding> Id,
    Id<Pipeline> PipelineId,
    string Repo,
    string Branch,
    string Path,
    string? CredentialsSecret,
    string? LastDeployedSha,
    string? LastSyncedSha,
    ReconcileStatus Status,
    DateTimeOffset CreatedAt,
    BindingDeployTrigger DeployTrigger = BindingDeployTrigger.Webhook,
    string? WebhookSecret = null) : IHasId<Id<PipelineConfigBinding>>;
