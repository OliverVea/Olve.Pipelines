using Olve.Pipelines.Sources;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>A delivery for a manifest <c>webhook</c> trigger, authenticated by the trigger's own verification over any request header.</summary>
[OperationHandler]
public sealed class WebhookTriggerHandler(WebhookTriggerService webhooks, IHttpContextAccessor http) : Contract.IWebhookTriggersReceiveHandler
{
    public async Task<Contract.WebhookTriggersReceiveResponse> HandleAsync(Contract.WebhookTriggersReceiveRequest request, CancellationToken cancellationToken)
    {
        var headers = http.HttpContext?.Request.Headers ?? new HeaderDictionary();
        if ((await WebhookDelivery.ReadAsync(request.Body, headers, cancellationToken)).TryPickProblems(out _, out var delivery))
            return new Contract.WebhookTriggersReceiveResponse.PayloadTooLarge();

        return await webhooks.ReceiveAsync(PipelineViews.PipelineId(request.PipelineId), request.Trigger, delivery, cancellationToken) switch
        {
            WebhookTriggerReceipt.NotFound => new Contract.WebhookTriggersReceiveResponse.NotFound(),
            WebhookTriggerReceipt.Unavailable => new Contract.WebhookTriggersReceiveResponse.ServiceUnavailable(),
            WebhookTriggerReceipt.Unauthorized => new Contract.WebhookTriggersReceiveResponse.Unauthorized(),
            WebhookTriggerReceipt.Busy => new Contract.WebhookTriggersReceiveResponse.TooManyRequests(),
            _ => new Contract.WebhookTriggersReceiveResponse.Accepted(),
        };
    }
}
