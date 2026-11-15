using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>Open pull requests by their latest CI run (failing, running) and the merge queue's length.</summary>
public sealed record CiHealth(int Open, int Failing, int Running, int Queued);

/// <summary>A pipeline's <see cref="CiHealth"/>: null when its active version declares no CI. Stateless query; transient.</summary>
public class CiHealthService(
    PipelineActivationService activations,
    CiPlanCache plans,
    PullRequestService pulls,
    CiRunService runs,
    CiQueueService queue)
{
    public CiHealth? Get(Id<Pipeline> pipelineId)
    {
        if (!activations.TryGet(pipelineId, out var activation) || activation is not PipelineActivation.Active active
            || plans.Get(active.VersionId).TryPickProblems(out _))
            return null;

        var open = pulls.ListByPipeline(pipelineId).Where(p => p.State == PullRequestState.Open).ToList();
        var latest = open.Select(p => runs.ListByPull(pipelineId, p.Number).LastOrDefault()).OfType<CiRun>().ToList();
        return new CiHealth(
            open.Count,
            latest.Count(r => r.Status is RunStatus.Failed),
            latest.Count(r => r.Status is RunStatus.Running),
            queue.Unfinished(pipelineId).Count);
    }
}
