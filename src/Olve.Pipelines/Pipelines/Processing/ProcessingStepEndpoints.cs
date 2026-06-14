using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public static class ProcessingStepEndpoints
{
    public record CreateProcessingStepRequest(string Name, int Order);
    public record SetStepConfigurationRequest(string Image, string Script, Dictionary<string, string>? EnvironmentVariables);
    public record UpdateOrderRequest(int Order);

    public static void MapProcessingStepEndpoints(this WebApplication app)
    {
        var pipelineGroup = app.MapGroup("/api/pipelines/{pipelineId}/processing");

        pipelineGroup.MapPost("/", Result<ProcessingStep> (
            PipelineService pipelines,
            PipelineConfigBindingService bindings,
            ProcessingStepService steps,
            Id<Pipeline> pipelineId,
            CreateProcessingStepRequest request) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return new ResultProblem($"Pipeline '{pipelineId}' not found.");
                if (bindings.IsBound(pipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(pipelineId);

                return steps.Create(pipelineId, request.Name, request.Order);
            })
            .WithResultMapping<ProcessingStep>()
            .WithName("CreateProcessingStep");

        pipelineGroup.MapGet("/", Result<ProcessingStep[]> (
            PipelineService pipelines,
            ProcessingStepService steps,
            Id<Pipeline> pipelineId) => pipelines.TryGet(pipelineId, out _) ? steps.GetByPipelineId(pipelineId) : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<ProcessingStep[]>()
            .WithName("ListProcessingSteps")
            .AllowAnonymous();

        var stepGroup = app.MapGroup("/api/processing-steps/{stepId}");

        stepGroup.MapGet("/", Result<ProcessingStep> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.TryGet(stepId))
            .WithResultMapping<ProcessingStep>()
            .WithName("GetProcessingStep")
            .AllowAnonymous();

        stepGroup.MapDelete("/", DeletionResult (
            ProcessingStepService steps,
            PipelineConfigBindingService bindings,
            Id<ProcessingStep> stepId) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out _, out var step))
                    return steps.Delete(stepId); // not found — preserve NotFound behavior
                return bindings.IsBound(step.PipelineId)
                    ? DeletionResult.Error(PipelineConfigBindingService.GitOnlyProblem(step.PipelineId))
                    : steps.Delete(stepId);
            })
            .WithDeletionMapping()
            .WithName("DeleteProcessingStep");

        stepGroup.MapPut("/order", Result<ProcessingStep> (
            ProcessingStepService steps,
            PipelineConfigBindingService bindings,
            Id<ProcessingStep> stepId,
            UpdateOrderRequest request) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
                    return problems;
                if (bindings.IsBound(step.PipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(step.PipelineId);

                return steps.UpdateOrder(stepId, request.Order);
            })
            .WithResultMapping<ProcessingStep>()
            .WithName("UpdateProcessingStepOrder");

        stepGroup.MapPut("/configuration", Result<StepConfiguration> (
            ProcessingStepService steps,
            PipelineConfigBindingService bindings,
            Id<ProcessingStep> stepId,
            SetStepConfigurationRequest request) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
                    return problems;
                if (bindings.IsBound(step.PipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(step.PipelineId);

                return steps.SetConfiguration(stepId, new StepConfiguration(request.Image, request.Script, request.EnvironmentVariables));
            })
            .WithResultMapping<StepConfiguration>()
            .WithName("SetProcessingStepConfiguration");

        stepGroup.MapGet("/configuration", Result<StepConfiguration> (
            ProcessingStepService steps,
            Id<ProcessingStep> stepId)
                => steps.TryGetConfiguration(stepId))
            .WithResultMapping<StepConfiguration>()
            .WithName("GetProcessingStepConfiguration")
            .AllowAnonymous();

        stepGroup.MapDelete("/configuration", Result (
            ProcessingStepService steps,
            PipelineConfigBindingService bindings,
            Id<ProcessingStep> stepId) =>
            {
                if (steps.TryGet(stepId).TryPickProblems(out var problems, out var step))
                    return problems;
                if (bindings.IsBound(step.PipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(step.PipelineId);

                return steps.RemoveConfiguration(stepId);
            })
            .WithResultMapping()
            .WithName("RemoveProcessingStepConfiguration");
    }
}
