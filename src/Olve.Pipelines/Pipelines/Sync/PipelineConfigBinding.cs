using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Binds a pipeline to its GitOps configuration source (a git repo). One binding per
/// pipeline; the repo is the sole source of truth for the pipeline's configuration.
///
/// The binding is the pipeline's git connection. From it the service runs a single
/// pull-based poll of the bound repo's branch head that is sequenced
/// <b>config-before-build</b>: on a head advance it first reconciles configuration (only
/// when the <c>.pipelines/</c> subtree changed) and then enqueues a production build for
/// the new commit. Config-apply gates the build. Because it is pull-based there is no
/// inbound webhook to authenticate, so the deploy trigger needs no secret.
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
/// </list>
/// </para>
/// <para>
/// The rest of <c>ReconcileStatus</c> (sync result, problems, secret state map) is added in
/// Phase 5 when it is first surfaced — intentionally absent here.
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
    DateTimeOffset CreatedAt) : IHasId<Id<PipelineConfigBinding>>;
