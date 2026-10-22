using Olve.Pipelines.Sources;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>Inbound deliveries for manifest <c>webhook</c> triggers. Public: authenticated by the trigger's own verification, not Bearer.</summary>
public static class WebhookTriggerEndpoints
{
    public static void MapWebhookTriggerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/hooks/{pipelineId}/{trigger}", async Task<IResult> (
                WebhookTriggerService webhooks, Id<Pipeline> pipelineId, string trigger, HttpRequest request, CancellationToken ct) =>
            {
                if ((await WebhookDelivery.ReadAsync(request, ct)).TryPickProblems(out _, out var delivery))
                    return HttpResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

                return await webhooks.ReceiveAsync(pipelineId, trigger, delivery, ct) switch
                {
                    WebhookTriggerReceipt.NotFound => HttpResults.NotFound(),
                    WebhookTriggerReceipt.Unavailable => HttpResults.StatusCode(StatusCodes.Status503ServiceUnavailable),
                    WebhookTriggerReceipt.Unauthorized => HttpResults.Unauthorized(),
                    _ => HttpResults.Accepted(),
                };
            })
            .WithName("ReceiveWebhookTrigger")
            .AllowAnonymous();
    }
}
