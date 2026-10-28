import "@typespec/http";
import "./errors.tsp";

using Http;

namespace Pipelines;

/** The delivery was taken; what it starts happens in the background. */
model DeliveryAccepted {
  @statusCode statusCode: 202;
}

/** The delivery is valid but not one this hook acts on. */
model DeliveryIgnored {
  @statusCode statusCode: 204;
}

/** The signature or the trigger's verification failed. */
model DeliveryUnauthorized {
  @statusCode statusCode: 401;
}

/** No such hook. */
model DeliveryNotFound {
  @statusCode statusCode: 404;
}

/** The body is over the hook's limit. */
model DeliveryTooLarge {
  @statusCode statusCode: 413;
}

/** Too many deliveries are being mapped; retry later. */
model DeliveryBusy {
  @statusCode statusCode: 429;
}

/** The delivery could not be queued; retry later. */
model DeliveryUnavailable {
  @statusCode statusCode: 503;
}

@tag("Bindings")
@route("/api/webhooks/binding")
interface BindingWebhooks {
  /** Receives the binding's GitHub push webhook, verified with the webhook's HMAC signature over the raw body (at most 5 MB). A push queues a push check (config first) and answers 202; other events answer 204. */
  @summary("GitHub push webhook")
  @useAuth(NoAuth)
  @post @route("{bindingId}/github") github(
    @path @format("uuid") bindingId: string,
    @header("X-Hub-Signature-256") signature?: string,
    @header("X-GitHub-Event") event?: string,
    @header contentType: "*/*",
    @body body: bytes,
  ): DeliveryAccepted | DeliveryIgnored | DeliveryUnauthorized | DeliveryNotFound | DeliveryTooLarge | DeliveryUnavailable;
}

@tag("Triggers")
@route("/api/hooks")
interface WebhookTriggers {
  /** A delivery for the pipeline's `onWebhook` trigger `trigger`, verified with the trigger's secret (any request header it names) over the raw body (at most 5 MB). Answers 202; the delivery is mapped to a run input in the background. */
  @summary("Deliver a webhook trigger")
  @useAuth(NoAuth)
  @post @route("{pipelineId}/{trigger}") receive(
    @path @format("uuid") pipelineId: string,
    @path trigger: string,
    @header contentType: "*/*",
    @body body: bytes,
  ): DeliveryAccepted | DeliveryUnauthorized | DeliveryNotFound | DeliveryTooLarge | DeliveryBusy | DeliveryUnavailable;

  /** Another system answers a step's external input with `from: [callback()]`. The URL comes from `ctx.inputUrl(step, input)` and is the credential: valid while the step waits for that input. The body (at most 64 KB) is the value as JSON, or `{"$decline": "reason"}` to fail the step without running it. Answers 202. */
  @summary("Answer a callback input")
  @useAuth(NoAuth)
  @post @route("inputs/{runId}/{step}/{input}/{mac}") answerCallback(
    @path @format("uuid") runId: string,
    @path step: string,
    @path input: string,
    @path mac: string,
    @header contentType: "*/*",
    @body body: bytes,
  ): DeliveryAccepted | NotFound | BadRequest | Conflict | DeliveryTooLarge;
}
