using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Olve.Pipelines.Streaming;

internal static class SseStreams
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    /// <summary>SseItem can't write a <c>:</c> comment, so the keep-alive is a named event nobody needs to listen for.</summary>
    public static SseItem<string> Heartbeat() => new("", "ping");

    /// <summary>Items from <paramref name="reader"/>, a heartbeat after each idle <paramref name="interval"/>; ends when the channel completes or on cancellation.</summary>
    public static async IAsyncEnumerable<SseItem<string>> WithHeartbeat(
        ChannelReader<SseItem<string>> reader, TimeProvider time, TimeSpan interval, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            while (reader.TryRead(out var item))
                yield return item;

            switch (await WaitAsync(reader, time, interval, ct))
            {
                case WaitOutcome.Timeout:
                    yield return Heartbeat();
                    break;
                case WaitOutcome.Done:
                    yield break;
            }
        }
    }

    /// <summary>Waits <paramref name="delay"/>; false when cancelled.</summary>
    public static async Task<bool> DelayAsync(TimeSpan delay, TimeProvider time, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<WaitOutcome> WaitAsync(ChannelReader<SseItem<string>> reader, TimeProvider time, TimeSpan interval, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(interval, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await reader.WaitToReadAsync(linked.Token) ? WaitOutcome.Ready : WaitOutcome.Done;
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested ? WaitOutcome.Done : WaitOutcome.Timeout;
        }
    }

    private enum WaitOutcome { Ready, Timeout, Done }
}
