using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Sources;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>The binding's GitHub push webhook, verified by HMAC over the raw body. On the public ingress, with /api/hooks.</summary>
[OperationHandler]
public sealed class BindingWebhookHandler(
    BindingWebhookReceiver receiver,
    PushTriggerService pushTriggers,
    IHttpContextAccessor http,
    TimeProvider time,
    ILogger<BindingWebhookHandler> logger) : Contract.IBindingWebhooksGithubHandler
{
    public async Task<Contract.BindingWebhooksGithubResponse> HandleAsync(Contract.BindingWebhooksGithubRequest request, CancellationToken cancellationToken)
    {
        var headers = http.HttpContext?.Request.Headers ?? new HeaderDictionary();
        if ((await WebhookDelivery.ReadAsync(request.Body, headers, cancellationToken)).TryPickProblems(out _, out var delivery))
            return new Contract.BindingWebhooksGithubResponse.PayloadTooLarge();

        var receivedAt = time.GetUtcNow();
        var bindingId = new Id<PipelineConfigBinding>(new Id(request.BindingId));
        var (action, pipelineId, push) = receiver.Evaluate(bindingId, delivery.Body, request.Signature, request.Event);
        switch (action)
        {
            case BindingWebhookAction.NotFound:
                return new Contract.BindingWebhooksGithubResponse.NotFound();
            case BindingWebhookAction.InvalidSignature:
                return new Contract.BindingWebhooksGithubResponse.Unauthorized();
            case BindingWebhookAction.Ignore:
                return new Contract.BindingWebhooksGithubResponse.NoContent();
        }

        // GitHub gives a delivery 10 s: queue the push check (config first) and acknowledge.
        if (pushTriggers.RequestCheck(pipelineId, new PushNotice(receivedAt, push)).TryPickProblems(out var problems))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Binding webhook for '{BindingId}' could not queue a push check", bindingId);
            return new Contract.BindingWebhooksGithubResponse.ServiceUnavailable();
        }

        logger.LogInformation("Binding webhook for '{BindingId}' queued a push check (config first)", bindingId);
        return new Contract.BindingWebhooksGithubResponse.Accepted();
    }
}
