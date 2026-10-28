using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Sources;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>
/// The inbound GitHub push webhook. Authenticated by HMAC over the raw body (not Bearer), so it
/// binds HttpRequest and returns raw status codes; it stays outside the contract, which has no
/// raw bodies. On the public ingress, with /api/hooks.
/// </summary>
public static class PipelineBindingEndpoints
{
    public static void MapPipelineBindingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhooks/binding/{bindingId}/github", async Task<IResult> (
                BindingWebhookReceiver receiver,
                PushTriggerService pushTriggers,
                ILoggerFactory loggerFactory,
                TimeProvider time,
                Id<PipelineConfigBinding> bindingId,
                HttpRequest httpRequest,
                CancellationToken ct) =>
            {
                if ((await WebhookDelivery.ReadAsync(httpRequest, ct)).TryPickProblems(out _, out var delivery))
                    return HttpResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var receivedAt = time.GetUtcNow();
                var (action, pipelineId, push) = receiver.Evaluate(
                    bindingId, delivery.Body, delivery.Header(GitHubWebhookSignature.HeaderName), delivery.Header("X-GitHub-Event"));
                switch (action)
                {
                    case BindingWebhookAction.NotFound:
                        return HttpResults.NotFound();
                    case BindingWebhookAction.InvalidSignature:
                        return HttpResults.Unauthorized();
                    case BindingWebhookAction.Ignore:
                        return HttpResults.NoContent();
                }

                // GitHub gives a delivery 10 s: queue the push check (config first) and acknowledge.
                var logger = loggerFactory.CreateLogger("BindingWebhook");
                if (pushTriggers.RequestCheck(pipelineId, new PushNotice(receivedAt, push)).TryPickProblems(out var problems))
                {
                    logger.LogProblems(LogLevel.Warning, problems, "Binding webhook for '{BindingId}' could not queue a push check", bindingId);
                    return HttpResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                logger.LogInformation("Binding webhook for '{BindingId}' queued a push check (config first)", bindingId);
                return HttpResults.Accepted();
            })
            .WithName("ExecuteBindingGitHubWebhook")
            .WithTags("Bindings")
            .WithSummary("GitHub push webhook")
            .WithDescription("Receives the binding's GitHub push webhook. Anonymous, verified with the webhook's HMAC signature.")
            .AllowAnonymous();
    }
}
