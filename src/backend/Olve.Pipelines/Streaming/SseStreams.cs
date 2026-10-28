using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Olve.Pipelines.Streaming;

internal static class SseStreams
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>The channel's items as they come, with <paramref name="heartbeat"/> whenever none came for <paramref name="interval"/>.</summary>
    public static async IAsyncEnumerable<T> WithHeartbeat<T>(
        ChannelReader<T> reader, TimeProvider time, TimeSpan interval, Func<T> heartbeat, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            while (reader.TryRead(out var item))
                yield return item;

            switch (await WaitAsync(reader, time, interval, ct))
            {
                case WaitOutcome.Timeout:
                    yield return heartbeat();
                    break;
                case WaitOutcome.Done:
                    yield break;
            }
        }
    }

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

    private static async Task<WaitOutcome> WaitAsync<T>(ChannelReader<T> reader, TimeProvider time, TimeSpan interval, CancellationToken ct)
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
