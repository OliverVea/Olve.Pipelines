using Olve.MinimalApi;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Triggers;

public static class TriggerEndpoints
{
    public record CreateTriggerRequest(string Name, TriggerTarget Target);
    public record WebhookRequest(Id<ArtifactBundle>? ArtifactBundleId);

    public static void MapTriggerEndpoints(this WebApplication app)
    {
        var pipelineGroup = app.MapGroup("/api/pipelines/{pipelineId}/triggers");

        pipelineGroup.MapPost("/", Result<Trigger> (
            PipelineService pipelines,
            PipelineConfigBindingService bindings,
            TriggerService triggers,
            Id<Pipeline> pipelineId,
            CreateTriggerRequest request) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return new ResultProblem($"Pipeline '{pipelineId}' not found.");
                if (bindings.IsBound(pipelineId))
                    return PipelineConfigBindingService.GitOnlyProblem(pipelineId);

                return triggers.Create(pipelineId, request.Name, request.Target);
            })
            .WithResultMapping<Trigger>()
            .WithName("CreateTrigger");

        pipelineGroup.MapGet("/", Result<Trigger[]> (
            PipelineService pipelines,
            TriggerService triggers,
            Id<Pipeline> pipelineId) =>
                pipelines.TryGet(pipelineId, out _)
                    ? triggers.GetByPipelineId(pipelineId)
                    : new ResultProblem($"Pipeline '{pipelineId}' not found."))
            .WithResultMapping<Trigger[]>()
            .WithName("ListTriggers")
            .AllowAnonymous();

        var triggerGroup = app.MapGroup("/api/triggers/{triggerId}");

        triggerGroup.MapGet("/", Result<Trigger> (
            TriggerService triggers,
            Id<Trigger> triggerId) =>
                triggers.TryGet(triggerId))
            .WithResultMapping<Trigger>()
            .WithName("GetTrigger")
            .AllowAnonymous();

        triggerGroup.MapDelete("/", DeletionResult (
            TriggerService triggers,
            PipelineConfigBindingService bindings,
            Id<Trigger> triggerId) =>
            {
                if (triggers.TryGet(triggerId).TryPickProblems(out _, out var trigger))
                    return triggers.Delete(triggerId); // not found — preserve NotFound behavior
                return bindings.IsBound(trigger.PipelineId)
                    ? DeletionResult.Error(PipelineConfigBindingService.GitOnlyProblem(trigger.PipelineId))
                    : triggers.Delete(triggerId);
            })
            .WithDeletionMapping()
            .WithName("DeleteTrigger");

        var webhookGroup = app.MapGroup("/api/webhooks/{triggerId}");

        webhookGroup.MapPost("/", Result<Jobs.JobGroup> (
            TriggerExecutionService execution,
            Id<Trigger> triggerId,
            HttpRequest httpRequest,
            WebhookRequest? request) =>
            {
                var authHeader = httpRequest.Headers.Authorization.ToString();
                var secret = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authHeader["Bearer ".Length..]
                    : string.Empty;

                return execution.Execute(triggerId, secret, request?.ArtifactBundleId);
            })
            .WithResultMapping<Jobs.JobGroup>()
            .WithName("ExecuteWebhook")
            .AllowAnonymous();
    }
}
