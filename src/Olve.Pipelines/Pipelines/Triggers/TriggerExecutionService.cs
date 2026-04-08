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

        if (!pipelines.TryGet(trigger.PipelineId, out _))
            return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{trigger.PipelineId}' not found."));

        return trigger.Target switch
        {
            ProductionTriggerTarget => ExecuteProduction(trigger),
            ProcessingTriggerTarget processing => ExecuteProcessing(trigger, processing, artifactBundleId),
            _ => Result.Failure<JobGroup>(new ResultProblem($"Unknown trigger target type."))
        };
    }

    private Result<JobGroup> ExecuteProduction(Trigger trigger)
    {
        if (!productionSteps.HasConfiguredSteps(trigger.PipelineId))
            return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{trigger.PipelineId}' has no configured production steps."));

        var steps = productionSteps.GetByPipelineId(trigger.PipelineId);
        if (steps.TryPickProblems(out var problems, out var stepArray))
            return problems;

        var bundle = bundles.Create(trigger.PipelineId, ArtifactBundleStatus.Pending);
        var jobGroup = jobGroups.CreateProductionGroup(trigger.PipelineId, bundle.Id);

        foreach (var step in stepArray)
        {
            jobs.CreateProductionJob(trigger.PipelineId, jobGroup.Id, step.Id);
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
