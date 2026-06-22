using Olve.MinimalApi;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Shared;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Pipelines.Triggers;

public static class TriggerEndpoints
{
    public record WebhookRequest(Id<ArtifactBundle>? ArtifactBundleId);

    public static void MapTriggerEndpoints(this WebApplication app)
    {
        var pipelineGroup = app.MapGroup("/api/pipelines/{pipelineId}/triggers");

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

        // Inbound GitHub webhook receiver. Authenticated by HMAC over the raw body (not Bearer), so
        // it binds HttpRequest directly and returns raw status codes rather than a Result mapping.
        // This is the only endpoint intended to be exposed on the public ingress.
        var gitHubWebhookGroup = app.MapGroup("/api/webhooks/github/{triggerId}");

        gitHubWebhookGroup.MapPost("/", async Task<IResult> (
            GitHubWebhookReceiver receiver,
            TriggerExecutionService execution,
            ILoggerFactory loggerFactory,
            Id<Trigger> triggerId,
            HttpRequest httpRequest) =>
        {
            // Read the raw body once: HMAC needs the exact bytes, then the same bytes are parsed.
            using var buffer = new MemoryStream();
            await httpRequest.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();

            var signature = httpRequest.Headers[GitHubWebhookSignature.HeaderName].ToString();
            var eventType = httpRequest.Headers["X-GitHub-Event"].ToString();

            switch (receiver.Evaluate(triggerId, body, signature, eventType))
            {
                case GitHubWebhookAction.NotFound:
                    return HttpResults.NotFound();
                case GitHubWebhookAction.InvalidSignature:
                    return HttpResults.Unauthorized();
                case GitHubWebhookAction.Ignore:
                    return HttpResults.NoContent();
            }

            var logger = loggerFactory.CreateLogger("GitHubWebhook");
            var result = execution.ExecuteInternal(triggerId);
            if (result.TryPickProblems(out var problems))
            {
                // A reconcile pause (or other transient refusal) lands here. Return 503 so GitHub
                // retries the delivery rather than silently dropping the push.
                logger.LogProblems(LogLevel.Warning, problems,
                    "GitHub webhook for trigger '{TriggerId}' did not fire production", triggerId);
                return HttpResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            logger.LogInformation("GitHub webhook for trigger '{TriggerId}' fired production", triggerId);
            return HttpResults.Accepted();
        })
            .WithName("ExecuteGitHubWebhook")
            .AllowAnonymous();
    }
}
