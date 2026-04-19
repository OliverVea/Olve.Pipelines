using Olve.Pipelines.Pipelines.Processing;

namespace Olve.Pipelines.Jobs;

public class DownstreamTriggerService(
    JobGroupService jobGroupService,
    ProcessingStepService processingStepService,
    JobService jobService,
    ILogger<DownstreamTriggerService> logger)
{
    public void HandleGroupCompleted(Id<JobGroup> groupId)
    {
        if (!jobGroupService.TryGet(groupId, out var group) || group is null)
            return;

        switch (group)
        {
            case ProductionJobGroup production:
                TriggerFirstProcessingStep(production);
                break;

            case ProcessingJobGroup processing:
                TriggerNextProcessingStep(processing);
                break;
        }
    }

    private void TriggerFirstProcessingStep(ProductionJobGroup group)
    {
        var stepsResult = processingStepService.GetByPipelineId(group.PipelineId);
        if (stepsResult.TryPickProblems(out _, out var steps) || steps.Length == 0)
        {
            logger.LogInformation(
                "Pipeline '{PipelineId}' has no processing steps — production complete",
                group.PipelineId);
            return;
        }

        var firstStep = steps[0];

        if (processingStepService.TryGetConfiguration(firstStep.Id).TryPickProblems(out _))
        {
            logger.LogWarning(
                "First processing step '{StepId}' has no configuration — skipping downstream trigger",
                firstStep.Id);
            return;
        }

        CreateProcessingJob(group.PipelineId, group.ArtifactBundleId, firstStep);
    }

    private void TriggerNextProcessingStep(ProcessingJobGroup group)
    {
        var stepsResult = processingStepService.GetByPipelineId(group.PipelineId);
        if (stepsResult.TryPickProblems(out _, out var steps))
            return;

        var currentIndex = Array.FindIndex(steps, s => s.Id == group.ProcessingStepId);
        if (currentIndex < 0 || currentIndex >= steps.Length - 1)
        {
            logger.LogInformation(
                "Pipeline '{PipelineId}' processing complete — no more steps after '{StepId}'",
                group.PipelineId,
                group.ProcessingStepId);
            return;
        }

        var nextStep = steps[currentIndex + 1];

        if (processingStepService.TryGetConfiguration(nextStep.Id).TryPickProblems(out _))
        {
            logger.LogWarning(
                "Next processing step '{StepId}' has no configuration — skipping downstream trigger",
                nextStep.Id);
            return;
        }

        CreateProcessingJob(group.PipelineId, group.ArtifactBundleId, nextStep);
    }

    private void CreateProcessingJob(Id<Pipelines.Pipeline> pipelineId, Id<Pipelines.Building.ArtifactBundle> artifactBundleId, ProcessingStep step)
    {
        var jobGroup = jobGroupService.CreateProcessingGroup(pipelineId, artifactBundleId, step.Id);
        jobService.CreateProcessingJob(pipelineId, jobGroup.Id, artifactBundleId, step.Id);

        logger.LogInformation(
            "Triggered processing step '{StepName}' (order {Order}) for pipeline '{PipelineId}'",
            step.Name,
            step.Order,
            pipelineId);
    }
}
