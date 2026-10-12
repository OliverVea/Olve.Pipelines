using Olve.MinimalApi;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public static class ProcessingStepEndpoints
{
    public record SetPromotionRequest(bool Blocked);

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
        // and what Promote would use without an N+1 of per-step calls. Includes every step.
        pipelineGroup.MapGet("/promotions", Result<StepPromotionState[]> (
            PipelineService pipelines,
            PromotionService promotions,
            Id<Pipeline> pipelineId) => pipelines.TryGet(pipelineId, out _)
                ? promotions.ListStates(pipelineId)
                : new ResultProblem($"Pipeline '{pipelineId}' not found."))
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
        // configuration is git-owned, but blocking/unblocking and promoting are operator actions.
        stepGroup.MapGet("/promotion", Result<StepPromotionState> (
            PromotionService promotions,
            Id<ProcessingStep> stepId) => promotions.GetState(stepId))
            .WithResultMapping<StepPromotionState>()
            .WithName("GetProcessingStepPromotion")
            .AllowAnonymous();

        // Block, or unblock. Unblocking a blocked step also starts the promotion that is due, if any;
        // the response's `promotion` says whether it did and with which bundle.
        stepGroup.MapPut("/promotion", Result<PromotionState> (
            PromotionService promotions,
            Id<ProcessingStep> stepId,
            SetPromotionRequest request) => promotions.SetBlocked(stepId, request.Blocked))
            .WithResultMapping<PromotionState>()
            .WithName("SetProcessingStepPromotion");

        // Promote: run the step with the bundle that last succeeded at the step before it (for the
        // first step, the latest completed production run). `expectedArtifactBundleId` is the bundle
        // the caller was shown; when given, the promote is refused if the server would use another.
        stepGroup.MapPost("/promote", Result<PromoteResult> (
            PromotionService promotions,
            Id<ProcessingStep> stepId,
            Id<ArtifactBundle>? expectedArtifactBundleId) => promotions.Promote(stepId, expectedArtifactBundleId))
            .WithResultMapping<PromoteResult>()
            .WithName("PromoteProcessingStep");

        // Removed: re-promote re-ran a step with its OWN last bundle, which redeployed the previous
        // release after a staged release (#41). Kept only so callers on an older `pl` get a clear
        // error instead of a 404; it never starts a job. Not in the OpenAPI document.
        stepGroup.MapPost("/re-promote", Result<JobGroup> (Id<ProcessingStep> stepId)
                => new ResultProblem(
                    "re-promote has been removed. Use POST /api/processing-steps/<stepId>/promote (`pl processing promote <stepId>`), " +
                    "which runs the bundle that last succeeded at the previous step, not this step's own last bundle. " +
                    "See `pl processing promotion <stepId>` for the bundle it would use."))
            .WithResultMapping<JobGroup>()
            .ExcludeFromDescription();
    }
}
