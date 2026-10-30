using Olve.Pipelines.Pipelines;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Streaming;

/// <summary>Change notifications for one pipeline or all; the stream ends with the request or the service.</summary>
[OperationHandler]
public sealed class StreamEventsHandler(ChangeBroadcaster broadcaster) : Contract.IEventsStreamHandler
{
    public Task<Contract.EventsStreamResponse> HandleAsync(Contract.EventsStreamRequest request, CancellationToken cancellationToken)
    {
        Id<Pipeline>? filter = request.Pipeline is { } id ? PipelineViews.PipelineId(id) : null;
        return Task.FromResult<Contract.EventsStreamResponse>(
            new Contract.EventsStreamResponse.Ok(broadcaster.StreamAsync(filter, request.LastEventId, cancellationToken)));
    }
}
