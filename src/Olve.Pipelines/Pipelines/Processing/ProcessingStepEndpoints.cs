using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public static class ProcessingStepEndpoints
{
    public record CreateProcessingStepRequest(string Name, int Order);
    public record SetStepConfigurationRequest(string Image, string Script, Dictionary<string, string>? EnvironmentVariables);
    public record UpdateOrderRequest(int Order);

    public static void MapProcessingStepEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/processing");

        group.MapPost("/", Result<ProcessingStep> (
            PipelineService pipelines,
            ProcessingStepService steps,
            Id<Pipeline> pipelineId,
            CreateProcessingStepRequest request) => pipelines.TryGet(pipelineId, out _) ? steps.Create(pipelineId, request.Name, request.Order) : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<ProcessingStep>();

        group.MapGet("/{stepId}", Result<ProcessingStep> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.TryGet(stepId))
            .WithResultMapping<ProcessingStep>()
            .AllowAnonymous();

        group.MapGet("/", Result<ProcessingStep[]> (
            PipelineService pipelines,
            ProcessingStepService steps,
            Id<Pipeline> pipelineId) => pipelines.TryGet(pipelineId, out _) ? steps.GetByPipelineId(pipelineId) : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<ProcessingStep[]>()
            .AllowAnonymous();

        group.MapDelete("/{stepId}", DeletionResult (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.Delete(stepId))
            .WithDeletionMapping();

        group.MapPut("/{stepId}/order", Result<ProcessingStep> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId,
            UpdateOrderRequest request)
                => steps.UpdateOrder(stepId, request.Order))
            .WithResultMapping<ProcessingStep>();

        group.MapPut("/{stepId}/configuration", Result<StepConfiguration> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId,
            SetStepConfigurationRequest request)
                => steps.SetConfiguration(stepId, new StepConfiguration(request.Image, request.Script, request.EnvironmentVariables)))
            .WithResultMapping<StepConfiguration>();

        group.MapGet("/{stepId}/configuration", Result<StepConfiguration> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.TryGetConfiguration(stepId))
            .WithResultMapping<StepConfiguration>()
            .AllowAnonymous();

        group.MapDelete("/{stepId}/configuration", Result (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.RemoveConfiguration(stepId))
            .WithResultMapping();
    }
}
