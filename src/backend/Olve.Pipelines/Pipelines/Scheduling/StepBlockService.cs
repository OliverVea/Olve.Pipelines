using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Block/unblock a step: an operator hold on the pipeline, for working on the pipeline itself or an
/// incident (not an approval mechanism). A blocked step holds its run at that stage: the scheduler
/// creates no attempt for it and marks the run <see cref="Runs.Run.HeldAt"/>. Unblocking fires the
/// attachment's <c>OnSet</c>, which resumes held runs (see <see cref="RunSchedulingEventRegistration"/>).
/// </summary>
public class StepBlockService(
    AttachmentStore<Pipeline, PipelineBlockedSteps> blocked,
    EntityStore<Pipeline> pipelines,
    RunSchedulingLock schedulingLock)
{
    public bool IsBlocked(Id<Pipeline> pipelineId, string stepName)
        => blocked.TryGet(pipelineId, out var b) && b.IsBlocked(stepName);

    public IReadOnlyList<string> ListBlocked(Id<Pipeline> pipelineId)
        => blocked.TryGet(pipelineId, out var b) ? b.BlockedSteps : [];

    public Result Block(Id<Pipeline> pipelineId, string stepName) => Change(pipelineId, stepName, block: true);

    public Result Unblock(Id<Pipeline> pipelineId, string stepName) => Change(pipelineId, stepName, block: false);

    private Result Change(Id<Pipeline> pipelineId, string stepName, bool block)
    {
        if (string.IsNullOrWhiteSpace(stepName))
            return new ResultProblem("Step name is required.");
        if (!pipelines.Contains(pipelineId))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);

        schedulingLock.Run(() =>
        {
            var current = ListBlocked(pipelineId);
            if (current.Contains(stepName, StringComparer.Ordinal) == block)
                return;

            var next = block
                ? current.Append(stepName).Order(StringComparer.Ordinal).ToList()
                : current.Where(s => s != stepName).ToList();

            if (next.Count == 0)
                blocked.Remove(pipelineId);
            else
                blocked.Set(pipelineId, new PipelineBlockedSteps(next));
        });

        return Result.Success();
    }
}
