using Olve.MinimalApi;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public static class ProcessingStepEndpoints
{
    public record PromotionState(bool Blocked);
    public record SetPromotionRequest(bool Blocked);
    public record StepPromotionState(Id<ProcessingStep> ProcessingStepId, bool Blocked);

    public static void MapProcessingStepEndpoints(this WebApplication app)
    {
        var pipelineGroup = app.MapGroup("/api/pipelines/{pipelineId}/processing");

        pipelineGroup.MapGet("/", Result<ProcessingStep[]> (
            PipelineService pipelines,
            ProcessingStepService steps,
            Id<Pipeline> pipelineId) => pipelines.TryGet(pipelineId, out _) ? steps.GetByPipelineId(pipelineId) : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<ProcessingStep[]>()
            .WithName("ListProcessingSteps")
            .AllowAnonymous();

        // Bulk promotion state for the whole pipeline — lets the flow view render every step's brake
        // without an N+1 of per-step calls. Includes every step (blocked and enabled).
        pipelineGroup.MapGet("/promotions", Result<StepPromotionState[]> (
            PipelineService pipelines,
            ProcessingStepService steps,
            PromotionGateService promotionGate,
            Id<Pipeline> pipelineId) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return new ResultProblem($"Pipeline '{pipelineId}' not found.");

                if (steps.GetByPipelineId(pipelineId).TryPickProblems(out var problems, out var stepArray))
                    return problems;

                return stepArray
                    .Select(s => new StepPromotionState(s.Id, promotionGate.IsBlocked(s.Id)))
                    .ToArray();
            })
            .WithResultMapping<StepPromotionState[]>()
            .WithName("ListProcessingStepPromotions")
            .AllowAnonymous();

        var stepGroup = app.MapGroup("/api/processing-steps/{stepId}");

        stepGroup.MapGet("/", Result<ProcessingStep> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.TryGet(stepId))
            .WithResultMapping<ProcessingStep>()
            .WithName("GetProcessingStep")
            .AllowAnonymous();

        stepGroup.MapGet("/configuration", Result<StepConfiguration> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.TryGetConfiguration(stepId))
            .WithResultMapping<StepConfiguration>()
            .WithName("GetProcessingStepConfiguration")
            .AllowAnonymous();

        // Promotion gate (operational state, not config). Available even on a bound pipeline:
        // configuration is git-owned, but blocking/unblocking promotion is an operator action.
        stepGroup.MapGet("/promotion", Result<PromotionState> (
            ProcessingStepService steps,
            PromotionGateService promotionGate,
            Id<ProcessingStep> stepId) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems))
                    return problems;

                return new PromotionState(promotionGate.IsBlocked(stepId));
            })
            .WithResultMapping<PromotionState>()
            .WithName("GetProcessingStepPromotion")
            .AllowAnonymous();

        stepGroup.MapPut("/promotion", Result<PromotionState> (
            ProcessingStepService steps,
            PromotionGateService promotionGate,
            Id<ProcessingStep> stepId,
            SetPromotionRequest request) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems))
                    return problems;

                promotionGate.Set(stepId, request.Blocked);
                return new PromotionState(request.Blocked);
            })
            .WithResultMapping<PromotionState>()
            .WithName("SetProcessingStepPromotion");

        // Re-promote: redrive the step's last-used artifact bundle. Reuses the processing-job
        // creation path; refuses when promotion is blocked or the step has never run.
        stepGroup.MapPost("/re-promote", Result<JobGroup> (
            ProcessingStepService steps,
            PromotionGateService promotionGate,
            ArtifactBundleService bundles,
            JobService jobs,
            Id<ProcessingStep> stepId) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
                    return problems;

                if (promotionGate.IsBlocked(stepId))
                    return Result.Failure<JobGroup>(new ResultProblem($"Processing step '{stepId}' has promotion blocked; re-promote refused."));

                if (jobs.GetLastPromotedBundle(step.PipelineId, stepId) is not { } bundleId)
                    return Result.Failure<JobGroup>(new ResultProblem($"Processing step '{stepId}' has no prior run to re-promote."));

                if (!bundles.TryGet(bundleId, out _))
                    return Result.Failure<JobGroup>(new ResultProblem($"Artifact bundle '{bundleId}' for step '{stepId}' no longer exists."));

                if (steps.TryGetConfiguration(stepId).TryPickProblems(out problems))
                    return problems;

                return jobs.CreateProcessingRun(step.PipelineId, bundleId, stepId);
            })
            .WithResultMapping<JobGroup>()
            .WithName("RePromoteProcessingStep");
    }
}
