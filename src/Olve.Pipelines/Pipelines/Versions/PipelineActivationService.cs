using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Versions.PipelineActivation;

namespace Olve.Pipelines.Pipelines.Versions;

/// <summary>
/// State transitions of a pipeline's single active version (spec §6.2). Pure state machine: deciding
/// when a drain is complete (no Scheduled/InProgress attempts on the old version) and cancelling
/// in-flight work belong to the scheduler/reconcile (B4/B6), which call <see cref="CompleteSwitch"/>.
/// Singleton: the lock makes each read-modify-write of the attachment atomic.
/// </summary>
public class PipelineActivationService(
    AttachmentStore<Pipeline, PipelineActivation> activations,
    PipelineVersionService versions,
    TimeProvider timeProvider)
{
    private readonly Lock _gate = new();

    public bool TryGet(Id<Pipeline> pipelineId, [NotNullWhen(true)] out PipelineActivation? activation)
        => activations.TryGet(pipelineId, out activation);

    /// <summary>
    /// Requests a switch to <paramref name="toVersionId"/>:
    /// <list type="bullet">
    /// <item>none → <c>Active(to)</c> (nothing to drain);</item>
    /// <item><c>Active(to)</c> → unchanged;</item>
    /// <item><c>Active(a)</c> → <c>Draining(a → to)</c>;</item>
    /// <item><c>Draining(a → b)</c>, to = a → <c>Active(a)</c> (rollback while draining: <c>a</c> never stopped owning its in-flight work, and nothing started on <c>b</c>);</item>
    /// <item><c>Draining(a → b)</c>, to = b → unchanged, except that <paramref name="cancelRunning"/> can be escalated to true;</item>
    /// <item><c>Draining(a → b)</c>, other → <c>Draining(a → to)</c> (retarget; latest wins).</item>
    /// </list>
    /// </summary>
    public Result<PipelineActivation> BeginSwitch(Id<Pipeline> pipelineId, Id<PipelineVersion> toVersionId, bool cancelRunning = false)
    {
        if (!versions.TryGet(toVersionId, out var to) || to.PipelineId != pipelineId)
            return new ResultProblem("Version '{0}' does not belong to pipeline '{1}'.", toVersionId, pipelineId);

        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            activations.TryGet(pipelineId, out var current);

            PipelineActivation next = current switch
            {
                null => new Active(to.Id, to.Number, now),
                Active a when a.VersionId == to.Id => a,
                Active a => new Draining(a.VersionId, a.VersionNumber, to.Id, to.Number, cancelRunning, now),
                Draining d when d.FromVersionId == to.Id => new Active(d.FromVersionId, d.FromVersionNumber, now),
                Draining d when d.ToVersionId == to.Id => d with { CancelRunning = d.CancelRunning || cancelRunning },
                Draining d => d with { ToVersionId = to.Id, ToVersionNumber = to.Number, CancelRunning = cancelRunning, Since = now },
                _ => throw new InvalidOperationException($"Unknown activation state '{current.GetType().Name}'."),
            };

            if (!Equals(next, current))
                activations.Set(pipelineId, next);

            return next;
        }
    }

    /// <summary>Completes a drain: <c>Draining(a → b)</c> → <c>Active(b)</c>. Fails in any other state.</summary>
    public Result<PipelineActivation> CompleteSwitch(Id<Pipeline> pipelineId)
    {
        lock (_gate)
        {
            if (!activations.TryGet(pipelineId, out var current) || current is not Draining d)
                return new ResultProblem("Pipeline '{0}' has no switch in progress.", pipelineId);

            var next = new Active(d.ToVersionId, d.ToVersionNumber, timeProvider.GetUtcNow());
            activations.Set(pipelineId, next);
            return next;
        }
    }
}
