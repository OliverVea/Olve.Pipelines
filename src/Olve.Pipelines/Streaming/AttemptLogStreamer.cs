using System.Text.Json;
using System.Net.ServerSentEvents;
using Amazon.Runtime;
using System.Runtime.CompilerServices;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Streaming;

/// <summary>
/// Live log of one attempt as SSE: polls the pod log (<see cref="IAttemptLiveLogs"/> returns a snapshot,
/// not a follow) and sends what is new as <c>log</c> chunks; once the attempt is terminal, the persisted
/// log's remainder and <c>end</c>. Transient.
/// </summary>
public class AttemptLogStreamer(AttemptService attempts, IAttemptLiveLogs liveLogs, JobLogService logs, TimeProvider time)
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public async IAsyncEnumerable<SseItem<string>> StreamAsync(Id<Attempt> attemptId, [EnumeratorCancellation] CancellationToken ct)
    {
        var sent = "";
        var idleSince = time.GetUtcNow();
        yield return new SseItem<string>(Json(new LogChunk("", Reset: true)), "log") { ReconnectionInterval = SseStreams.Retry };

        while (!ct.IsCancellationRequested)
        {
            if (!attempts.TryGet(attemptId, out var attempt))
            {
                yield return End("deleted");
                yield break;
            }

            var terminal = attempt.Status.IsTerminal();
            var text = await TryReadAsync(attempt, terminal, ct);
            if (ct.IsCancellationRequested)
                yield break;

            if (text is not null && text != sent)
            {
                yield return text.StartsWith(sent, StringComparison.Ordinal)
                    ? Log(new LogChunk(text[sent.Length..]))
                    : Log(new LogChunk(text, Reset: true));
                sent = text;
                idleSince = time.GetUtcNow();
            }

            if (terminal)
            {
                yield return End(attempt.Status.Discriminator());
                yield break;
            }

            if (time.GetUtcNow() - idleSince >= SseStreams.HeartbeatInterval)
            {
                yield return SseStreams.Heartbeat();
                idleSince = time.GetUtcNow();
            }

            if (!await SseStreams.DelayAsync(PollInterval, time, ct))
                yield break;
        }
    }

    private async Task<string?> TryReadAsync(Attempt attempt, bool terminal, CancellationToken ct)
    {
        try
        {
            if (!terminal)
                return await liveLogs.TryGetAsync(attempt, ct);
            return (await logs.GetLogsAsync(attempt.Id, ct)).TryPickProblems(out _, out var persisted) ? null : persisted;
        }
        catch (Exception e) when (e is OperationCanceledException or AmazonServiceException or HttpRequestException)
        {
            return null;
        }
    }

    private static SseItem<string> Log(LogChunk chunk) => new(Json(chunk), "log");

    private static SseItem<string> End(string status)
        => new(JsonSerializer.Serialize(new LogStreamEnd(status), StreamingJsonContext.Default.LogStreamEnd), "end");

    private static string Json(LogChunk chunk) => JsonSerializer.Serialize(chunk, StreamingJsonContext.Default.LogChunk);
}
