using Olve.Pipelines.Configuration;
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Mvc;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Streaming;

/// <summary>Server-sent event streams: change notifications and live attempt logs.</summary>
public static class EventStreamEndpoints
{
    public static void MapEventStreamEndpoints(this WebApplication app)
    {
        // ?pipeline= limits the stream to one pipeline; omit it for all.
        app.MapGet("/api/events", (
                HttpContext http, ChangeBroadcaster broadcaster, IHostApplicationLifetime lifetime,
                Id<Pipeline>? pipeline, [FromHeader(Name = "Last-Event-ID")] string? lastEventId) =>
            {
                var ct = StreamToken(http, lifetime);
                return TypedResults.ServerSentEvents(broadcaster.StreamAsync(pipeline, lastEventId, ct));
            })
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream")
            .WithName("StreamEvents")
            .WithTags("Events")
            .WithSummary("Stream change events")
            .WithDescription("Server-sent change notifications for one pipeline (`pipeline`) or all; see Live events above.")
            .RequireRead();

        // Without ?attempt= the latest attempt. Ends with `end` once the attempt is terminal.
        app.MapGet("/api/pipelines/{pipelineId}/runs/{number:int}/steps/{step}/logs/stream", IResult (
                HttpContext http, RunService runs, AttemptService attempts, AttemptLogStreamer streamer, IHostApplicationLifetime lifetime,
                Id<Pipeline> pipelineId, int number, string step, int? attempt) =>
            {
                if (!runs.TryGetByNumber(pipelineId, number, out var run))
                    return http.ToApiResult([new NotFoundProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId)]);
                var target = attempt is { } n
                    ? attempts.ListForStep(run.Id, step).FirstOrDefault(a => a.Number == n)
                    : attempts.ListForStep(run.Id, step).LastOrDefault();
                if (target is null)
                    return http.ToApiResult([new NotFoundProblem("Run #{0} has no such attempt of step '{1}'.", number, step)]);

                var ct = StreamToken(http, lifetime);
                return TypedResults.ServerSentEvents(streamer.StreamAsync(target.Id, ct));
            })
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream")
            .WithApiProblems()
            .WithName("StreamStepLogs")
            .WithTags("Steps")
            .WithSummary("Stream a step's log")
            .WithDescription("The attempt's log as server-sent events (`attempt`, default the latest): `log` events `{text, reset}`, then `end` `{status}` once the attempt is finished.")
            .RequireRead();
    }

    /// <summary>Unbuffered through proxies, and ended on shutdown so a long-lived stream never holds up a redeploy.</summary>
    private static CancellationToken StreamToken(HttpContext http, IHostApplicationLifetime lifetime)
    {
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        http.Response.RegisterForDispose(cts);
        return cts.Token;
    }
}
