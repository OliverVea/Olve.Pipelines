using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public static class ProductionStepEndpoints
{
    public static void MapProductionStepEndpoints(this WebApplication app)
    {
        var pipelineGroup = app.MapGroup("/api/pipelines/{pipelineId}/production");

        pipelineGroup.MapGet("/", Result<ProductionStep[]> (
            PipelineService pipelines,
            ProductionStepService steps,
            Id<Pipeline> pipelineId) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return Result.Failure<ProductionStep[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

                return steps.GetByPipelineId(pipelineId);
            })
            .WithResultMapping<ProductionStep[]>()
            .WithName("ListProductionSteps")
            .AllowAnonymous();

        var stepGroup = app.MapGroup("/api/production-steps/{stepId}");

        stepGroup.MapGet("/", Result<ProductionStep> (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.TryGet(stepId))
            .WithResultMapping<ProductionStep>()
            .WithName("GetProductionStep")
            .AllowAnonymous();

        stepGroup.MapGet("/configuration", Result<StepConfiguration> (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.TryGetConfiguration(stepId))
            .WithResultMapping<StepConfiguration>()
            .WithName("GetProductionStepConfiguration")
            .AllowAnonymous();
    }
}
