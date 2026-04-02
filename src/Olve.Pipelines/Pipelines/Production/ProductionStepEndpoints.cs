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
        var group = app.MapGroup("/api/pipelines/{pipelineId}/production");

        group.MapPost("/", Result<ProductionStep> (
            PipelineService pipelines,
            ProductionStepService steps,
            Id<Pipeline> pipelineId,
            CreateProductionStepRequest request) => pipelines.TryGet(pipelineId, out _) ? steps.Create(pipelineId, request.Name) : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<ProductionStep>();

        group.MapGet("/{stepId}", Result<ProductionStep> (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.TryGet(stepId))
            .WithResultMapping<ProductionStep>()
            .AllowAnonymous();

        group.MapGet("/", Result<ProductionStep[]> (
            PipelineService pipelines,
            ProductionStepService steps,
            Id<Pipeline> pipelineId) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return Result.Failure<ProductionStep[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

                return steps.GetByPipelineId(pipelineId);
            })
            .WithResultMapping<ProductionStep[]>()
            .AllowAnonymous();

        group.MapDelete("/{stepId}", DeletionResult (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.Delete(stepId))
            .WithDeletionMapping();

        group.MapPut("/{stepId}/configuration", Result<StepConfiguration> (
            ProductionStepService steps,
            Id<ProductionStep> stepId,
            SetStepConfigurationRequest request)
                => steps.SetConfiguration(stepId, new StepConfiguration(request.Image, request.Script, request.EnvironmentVariables)))
            .WithResultMapping<StepConfiguration>();

        group.MapGet("/{stepId}/configuration", Result<StepConfiguration> (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.TryGetConfiguration(stepId))
            .WithResultMapping<StepConfiguration>()
            .AllowAnonymous();

        group.MapDelete("/{stepId}/configuration", Result (
            ProductionStepService steps,
            Id<ProductionStep> stepId)
                => steps.RemoveConfiguration(stepId))
            .WithResultMapping();
    }
}
