using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// Binds a pipeline to its GitOps configuration source (a git repo). One binding per
/// pipeline; the repo is the sole source of truth for the pipeline's configuration.
///
/// The binding is the pipeline's git connection: from it the service derives two polls
/// of the bound repo — a <b>config</b> poll (watches <c>.pipelines/</c> → reconcile) and
/// a <b>deploy</b> poll (watches the branch head → fire production). Both are pull-based;
/// there is no inbound webhook to authenticate, so the deploy trigger needs no secret.
///
/// Source fields (repo, branch, path, credentials reference) and the deploy cursor land
/// in Phase 3 — this skeleton carries only identity + lifecycle.
/// </summary>
public record PipelineConfigBinding(
    Id<PipelineConfigBinding> Id,
    Id<Pipeline> PipelineId,
    DateTimeOffset CreatedAt) : IHasId<Id<PipelineConfigBinding>>;
