using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using static Olve.Pipelines.Jobs.Job;
using static Olve.Pipelines.Jobs.JobStatus;

namespace Olve.Pipelines.FailureHandlers;

/// <summary>
/// Runs a pipeline's failure handlers when a job group fails. Subscribed to
/// <see cref="JobEvents.OnGroupFailed"/> (the failure analog of
/// <see cref="DownstreamTriggerService.HandleGroupCompleted"/>).
///
/// Resolution is synchronous and pure; submission is best-effort, fire-and-forget K8s Jobs (untracked,
/// not first-class <see cref="Job"/> entities) so a handler's own failure cannot re-enter this path,
/// affect pipeline status, or collide with step scheduling. One run per matched binding — a
/// whole-pipeline handler fires once even when several steps in the group failed.
/// </summary>
public class FailureHandlerService(
    JobGroupService jobGroupService,
    JobService jobService,
    PipelineService pipelineService,
    ProductionStepService productionStepService,
    ProcessingStepService processingStepService,
    FailureHandlerBindingService bindingService,
    FailureHandlerLibrary library,
    IKubernetesClient kubernetesClient,
    KubernetesOptions options,
    ILogger<FailureHandlerService> logger)
{
    private sealed record FailedStep(string Name, FailedStepKind Kind, string Reason, string LogKey);

    public void HandleGroupFailed(Id<JobGroup> groupId)
    {
        if (!jobGroupService.TryGet(groupId, out var group) || group is null)
            return;

        var bindings = bindingService.Get(group.PipelineId);
        if (bindings.Count == 0)
            return;

        if (!pipelineService.TryGet(group.PipelineId, out var pipeline))
            return;

        var bundleId = group switch
        {
            ProductionJobGroup p => p.ArtifactBundleId,
            ProcessingJobGroup c => c.ArtifactBundleId,
            _ => (Id<ArtifactBundle>?)null,
        };
        if (bundleId is null)
            return;

        var failedSteps = ResolveFailedSteps(group, bundleId.Value);
        if (failedSteps.Count == 0)
        {
            logger.LogInformation("JobGroup '{GroupId}' failed but no failed step resolved — no handlers run", groupId);
            return;
        }

        var index = 0;
        foreach (var binding in bindings)
        {
            var representative = binding.IsWholePipeline
                ? failedSteps[0]
                : failedSteps.FirstOrDefault(s => binding.Steps.Contains(s.Name));

            if (representative is null)
                continue; // a per-step binding whose steps did not fail

            if (!library.TryGet(binding.HandlerName, out var configuration))
            {
                logger.LogWarning(
                    "Pipeline '{PipelineId}' binds unknown failure handler '{HandlerName}' — skipping",
                    group.PipelineId, binding.HandlerName);
                continue;
            }

            var context = new FailureContext(
                group.PipelineId,
                pipeline.Name,
                representative.Name,
                representative.Kind,
                representative.Reason,
                representative.LogKey);

            // Binding env first, then failure context — the PIPELINE_* contract is authoritative and
            // cannot be shadowed by operator config.
            var env = new Dictionary<string, string>(binding.Env);
            foreach (var (key, value) in context.ToEnvironmentVariables())
                env[key] = value;

            var jobName = HandlerJobName(groupId, index++);
            DispatchBestEffort(binding.HandlerName, jobName, configuration.Image, configuration.Script, env);
        }
    }

    private List<FailedStep> ResolveFailedSteps(JobGroup group, Id<ArtifactBundle> bundleId)
    {
        var failedSteps = new List<FailedStep>();

        foreach (var job in jobService.GetJobsByGroup(group.Id))
        {
            if (job.Status is not Failed failed)
                continue;

            string name;
            FailedStepKind kind;

            switch (job)
            {
                case ProductionJob production:
                    if (productionStepService.TryGet(production.ProductionStepId).TryPickProblems(out _, out var prodStep))
                        continue;
                    name = prodStep.Name;
                    kind = FailedStepKind.Production;
                    break;
                case ProcessingJob processing:
                    if (processingStepService.TryGet(processing.ProcessingStepId).TryPickProblems(out _, out var procStep))
                        continue;
                    name = procStep.Name;
                    kind = FailedStepKind.Processing;
                    break;
                default:
                    continue;
            }

            var logKey = KubernetesJobExecutor.LogS3Key(group.PipelineId, bundleId, job.Id);
            failedSteps.Add(new FailedStep(name, kind, failed.Reason ?? string.Empty, logKey));
        }

        return failedSteps;
    }

    private void DispatchBestEffort(string handlerName, string jobName, string image, string script, Dictionary<string, string> env)
    {
        // Fire-and-forget: the trigger event is synchronous and a handler is best-effort, so its own
        // failure to even submit must not surface to the caller or the pipeline. Errors are logged.
        _ = Task.Run(async () =>
        {
            try
            {
                await kubernetesClient.CreateBareJobAsync(options.Namespace, jobName, image, script, env);
                logger.LogInformation("Started failure handler '{HandlerName}' as Job '{JobName}'", handlerName, jobName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to start failure handler '{HandlerName}' (Job '{JobName}')", handlerName, jobName);
            }
        });
    }

    // Distinct prefix from step jobs (olve-{jobId}) so a handler run can never collide with step
    // scheduling. The group Id + binding index keeps it deterministic and unique within a group.
    private static string HandlerJobName(Id<JobGroup> groupId, int index)
    {
        var name = $"olve-fh-{groupId.Value.Value:N}-{index}";
        return name.Length > 63 ? name[..63] : name;
    }
}
