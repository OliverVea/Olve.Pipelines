using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public static class ProductionStepEndpoints
{
    public record CreateProductionStepRequest(string Name);
    public record SetStepConfigurationRequest(string Image, string Script, Dictionary<string, string>? EnvironmentVariables);

    public static void MapProductionStepEndpoints(this WebApplication app)
    {
        var pipelineGroup = app.MapGroup("/api/pipelines/{pipelineId}/production");

        pipelineGroup.MapPost("/", Result<ProductionStep> (
            PipelineService pipelines,
            ProductionStepService steps,
            Id<Pipeline> pipelineId,
            CreateProductionStepRequest request) => pipelines.TryGet(pipelineId, out _) ? steps.Create(pipelineId, request.Name) : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<ProductionStep>()
            .WithName("CreateProductionStep");

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

        stepGroup.MapDelete("/", DeletionResult (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.Delete(stepId))
            .WithDeletionMapping()
            .WithName("DeleteProductionStep");

        stepGroup.MapPut("/configuration", Result<StepConfiguration> (
            ProductionStepService steps,
            Id<ProductionStep> stepId,
            SetStepConfigurationRequest request)
                => steps.SetConfiguration(stepId, new StepConfiguration(request.Image, request.Script, request.EnvironmentVariables)))
            .WithResultMapping<StepConfiguration>()
            .WithName("SetProductionStepConfiguration");

        stepGroup.MapGet("/configuration", Result<StepConfiguration> (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.TryGetConfiguration(stepId))
            .WithResultMapping<StepConfiguration>()
            .WithName("GetProductionStepConfiguration")
            .AllowAnonymous();

        stepGroup.MapDelete("/configuration", Result (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.RemoveConfiguration(stepId))
            .WithResultMapping()
            .WithName("RemoveProductionStepConfiguration");
    }
}
