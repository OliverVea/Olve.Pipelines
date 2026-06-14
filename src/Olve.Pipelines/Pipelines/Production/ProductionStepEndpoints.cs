using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
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
            PipelineConfigBindingService bindings,
            ProductionStepService steps,
            Id<Pipeline> pipelineId,
            CreateProductionStepRequest request) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return new ResultProblem($"Pipeline '{pipelineId}' not found.");
                if (bindings.IsBound(pipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(pipelineId);

                return steps.Create(pipelineId, request.Name);
            })
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
            PipelineConfigBindingService bindings,
            Id<ProductionStep> stepId) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out _, out var step))
                    return steps.Delete(stepId); // not found — preserve NotFound behavior
                return bindings.IsBound(step.PipelineId)
                    ? DeletionResult.Error(PipelineConfigBindingService.GitOnlyProblem(step.PipelineId))
                    : steps.Delete(stepId);
            })
            .WithDeletionMapping()
            .WithName("DeleteProductionStep");

        stepGroup.MapPut("/configuration", Result<StepConfiguration> (
            ProductionStepService steps,
            PipelineConfigBindingService bindings,
            Id<ProductionStep> stepId,
            SetStepConfigurationRequest request) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
                    return problems;
                if (bindings.IsBound(step.PipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(step.PipelineId);

                return steps.SetConfiguration(stepId, new StepConfiguration(request.Image, request.Script, request.EnvironmentVariables));
            })
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
            PipelineConfigBindingService bindings,
            Id<ProductionStep> stepId) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
                    return problems;
                if (bindings.IsBound(step.PipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(step.PipelineId);

                return steps.RemoveConfiguration(stepId);
            })
            .WithResultMapping()
            .WithName("RemoveProductionStepConfiguration");
    }
}
