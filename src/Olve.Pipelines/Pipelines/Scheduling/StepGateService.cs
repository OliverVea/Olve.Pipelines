using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Block/unblock promotion gates (spec §5.3). A blocked step holds its run at that stage: the scheduler
/// creates no attempt for it and marks the run <see cref="Runs.Run.HeldAt"/>. Unblocking fires the
/// attachment's <c>OnSet</c>, which resumes held runs (see <see cref="RunSchedulingEventRegistration"/>).
/// The API is B8; these are the services.
/// </summary>
public class StepGateService(
    AttachmentStore<Pipeline, PipelineStepGates> gates,
    EntityStore<Pipeline> pipelines,
    RunSchedulingLock schedulingLock)
{
    public bool IsBlocked(Id<Pipeline> pipelineId, string stepName)
        => gates.TryGet(pipelineId, out var g) && g.IsBlocked(stepName);

    public IReadOnlyList<string> ListBlocked(Id<Pipeline> pipelineId)
        => gates.TryGet(pipelineId, out var g) ? g.BlockedSteps : [];

    public Result Block(Id<Pipeline> pipelineId, string stepName) => Change(pipelineId, stepName, block: true);

    public Result Unblock(Id<Pipeline> pipelineId, string stepName) => Change(pipelineId, stepName, block: false);

    private Result Change(Id<Pipeline> pipelineId, string stepName, bool block)
    {
        if (string.IsNullOrWhiteSpace(stepName))
            return new ResultProblem("Step name is required.");
        if (!pipelines.Contains(pipelineId))
            return new ResultProblem("Pipeline '{0}' not found.", pipelineId);

        schedulingLock.Run(() =>
        {
            var current = ListBlocked(pipelineId);
            if (current.Contains(stepName, StringComparer.Ordinal) == block)
                return;

            var next = block
                ? current.Append(stepName).Order(StringComparer.Ordinal).ToList()
                : current.Where(s => s != stepName).ToList();

            if (next.Count == 0)
                gates.Remove(pipelineId);
            else
                gates.Set(pipelineId, new PipelineStepGates(next));
        });

        return Result.Success();
    }
}
