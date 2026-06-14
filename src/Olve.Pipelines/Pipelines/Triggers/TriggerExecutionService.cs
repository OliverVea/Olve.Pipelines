using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Triggers;

public class TriggerExecutionService(
    EntityStore<Trigger> store,
    PipelineService pipelines,
    ProductionStepService productionSteps,
    ProcessingStepService processingSteps,
    ArtifactBundleService bundles,
    JobGroupService jobGroups,
    JobService jobs)
{
    public Result<JobGroup> Execute(Id<Trigger> triggerId, string secret, Id<ArtifactBundle>? artifactBundleId)
    {
        if (!store.TryGet(triggerId, out var trigger))
            return Result.Failure<JobGroup>(new ResultProblem($"Trigger '{triggerId}' not found."));

        if (trigger.Secret != secret)
            return Result.Failure<JobGroup>(new ResultProblem("Invalid secret."));

        return ExecuteInternal(trigger, artifactBundleId);
    }

    public Result<JobGroup> ExecuteInternal(Id<Trigger> triggerId)
    {
        if (!store.TryGet(triggerId, out var trigger))
            return Result.Failure<JobGroup>(new ResultProblem($"Trigger '{triggerId}' not found."));

        return ExecuteInternal(trigger, artifactBundleId: null);
    }

    private Result<JobGroup> ExecuteInternal(Trigger trigger, Id<ArtifactBundle>? artifactBundleId)
    {
        if (!pipelines.TryGet(trigger.PipelineId, out _))
            return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{trigger.PipelineId}' not found."));

        return trigger.Target switch
        {
            ProductionTriggerTarget => ExecuteProduction(trigger),
            ProcessingTriggerTarget processing => ExecuteProcessing(trigger, processing, artifactBundleId),
            PollTriggerTarget => ExecuteProduction(trigger),
            _ => Result.Failure<JobGroup>(new ResultProblem($"Unknown trigger target type."))
        };
    }

    private Result<JobGroup> ExecuteProduction(Trigger trigger)
        => ExecuteProductionForPipeline(trigger.PipelineId);

    /// <summary>
    /// Starts a production run for a pipeline directly (no <see cref="Trigger"/> entity). Used by
    /// the binding-derived deploy poll, whose deploy "trigger" is the bound repo's branch head.
    /// </summary>
    public Result<JobGroup> ExecuteProductionForPipeline(Id<Pipeline> pipelineId)
    {
        if (!pipelines.TryGet(pipelineId, out _))
            return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

        if (!productionSteps.HasConfiguredSteps(pipelineId))
            return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{pipelineId}' has no configured production steps."));

        var steps = productionSteps.GetByPipelineId(pipelineId);
        if (steps.TryPickProblems(out var problems, out var stepArray))
            return problems;

        var bundle = bundles.Create(pipelineId, ArtifactBundleStatus.Pending);
        var jobGroup = jobGroups.CreateProductionGroup(pipelineId, bundle.Id);

        foreach (var step in stepArray)
        {
            jobs.CreateProductionJob(pipelineId, jobGroup.Id, step.Id);
        }

        return jobGroup;
    }

    private Result<JobGroup> ExecuteProcessing(Trigger trigger, ProcessingTriggerTarget target, Id<ArtifactBundle>? artifactBundleId)
    {
        if (artifactBundleId is not { } bundleId)
            return Result.Failure<JobGroup>(new ResultProblem("Processing triggers require an artifactBundleId."));

        if (!bundles.TryGet(bundleId, out var bundle))
            return Result.Failure<JobGroup>(new ResultProblem($"Artifact bundle '{bundleId}' not found."));

        if (bundle.PipelineId != trigger.PipelineId)
            return Result.Failure<JobGroup>(new ResultProblem($"Artifact bundle '{bundleId}' does not belong to pipeline '{trigger.PipelineId}'."));

        var stepResult = processingSteps.TryGet(target.ProcessingStepId);
        if (stepResult.TryPickProblems(out var problems, out var step))
            return problems;

        if (step.PipelineId != trigger.PipelineId)
            return Result.Failure<JobGroup>(new ResultProblem($"Processing step '{target.ProcessingStepId}' does not belong to pipeline '{trigger.PipelineId}'."));

        var configResult = processingSteps.TryGetConfiguration(step.Id);
        if (configResult.TryPickProblems(out problems))
            return problems;

        var jobGroup = jobGroups.CreateProcessingGroup(trigger.PipelineId, bundleId, step.Id);
        jobs.CreateProcessingJob(trigger.PipelineId, jobGroup.Id, bundleId, step.Id);

        return jobGroup;
    }
}
