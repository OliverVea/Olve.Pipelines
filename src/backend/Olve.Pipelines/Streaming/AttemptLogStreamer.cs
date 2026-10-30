using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Streaming;

/// <summary>
/// An attempt's log as server-sent events: the live pod log while it runs (appended as it grows, replaced when it
/// was rewritten), the stored log once it is terminal, then <c>end</c> with the final status.
/// </summary>
public class AttemptLogStreamer(AttemptService attempts, IAttemptLiveLogs liveLogs, JobLogService logs, PodChangeSignals podChanges, TimeProvider time)
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public async IAsyncEnumerable<ServerSentEvent<Contract.LogEvent>> StreamAsync(Id<Attempt> attemptId, [EnumeratorCancellation] CancellationToken ct)
    {
        var sent = "";
        var idleSince = time.GetUtcNow();
        yield return Log("", reset: true);

        while (!ct.IsCancellationRequested)
        {
            if (!attempts.TryGet(attemptId, out var attempt))
            {
                yield return End(Contract.LogEndStatus.Deleted);
                yield break;
            }

            var terminal = attempt.Status.IsTerminal();
            var changed = podChanges.Arm(attempt.KubernetesJobName);
            var text = await TryReadAsync(attempt, terminal, ct);
            if (ct.IsCancellationRequested)
            {
                await podChanges.WaitAsync(attempt.KubernetesJobName, changed, TimeSpan.Zero, CancellationToken.None);
                yield break;
            }

            if (text is not null && text != sent)
            {
                yield return text.StartsWith(sent, StringComparison.Ordinal)
                    ? Log(text[sent.Length..], reset: false)
                    : Log(text, reset: true);
                sent = text;
                idleSince = time.GetUtcNow();
            }

            if (terminal)
            {
                await podChanges.WaitAsync(attempt.KubernetesJobName, changed, TimeSpan.Zero, CancellationToken.None);
                yield return End(attempt.Status.State().ToLogEnd());
                yield break;
            }

            if (time.GetUtcNow() - idleSince >= SseStreams.HeartbeatInterval)
            {
                yield return new ServerSentEvent<Contract.LogEvent>(new Contract.LogPing());
                idleSince = time.GetUtcNow();
            }

            try
            {
                await podChanges.WaitAsync(attempt.KubernetesJobName, changed, PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
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

    private static ServerSentEvent<Contract.LogEvent> Log(string text, bool reset) => new(new Contract.LogChunk { Text = text, Reset = reset });

    private static ServerSentEvent<Contract.LogEvent> End(Contract.LogEndStatus status) => new(new Contract.LogStreamEnd { Status = status });
}
