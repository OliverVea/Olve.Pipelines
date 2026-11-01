using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Streaming;
using Olve.Results;
using Olve.Results.TUnit;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Streaming;

public class AttemptLogStreamerTests
{
    private sealed class ScriptedLiveLogs(params string?[] snapshots) : IAttemptLiveLogs
    {
        private int _next;
        public int Reads => _next;
        public Task<string?> TryGetAsync(Attempt attempt, CancellationToken ct)
            => Task.FromResult(snapshots[Math.Min(_next++, snapshots.Length - 1)]);
    }

    /// <summary>Says when something starts waiting on it, and never lets the wait end.</summary>
    private sealed class HeldClock : TimeProvider
    {
        public TaskCompletionSource Armed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Armed.TrySetResult();
            return new Held();
        }

        private sealed class Held : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Catches: a stream that reads again at once, so each open stream hammers the K8s API and storage in a tight loop.
    [Test]
    public async Task ARunningAttemptsStream_WaitsBetweenReads()
    {
        var domain = new TestRunDomain();
        var attempt = domain.Schedule();
        TestRunDomain.Pick(domain.AttemptService.MarkInProgress(attempt.Id));
        var live = new ScriptedLiveLogs("same\n");
        var s3 = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", MaxErrorRetry = 0 });
        var clock = new HeldClock();
        var streamer = new AttemptLogStreamer(domain.AttemptService, live,
            new JobLogService(s3, new StorageOptions("b"), domain.AttemptService, live), new PodChangeSignals(clock), clock)
        {
            PollInterval = TimeSpan.FromHours(1),
        };
        using var cts = new CancellationTokenSource();

        var drained = Task.Run(async () =>
        {
            await foreach (var _ in streamer.StreamAsync(attempt.Id, cts.Token)) { }
        });
        await clock.Armed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reads = live.Reads;
        await cts.CancelAsync();
        await drained.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(reads).IsEqualTo(1);
    }

    [Test]
    public async Task StreamsNewText_ResetsOnRewrite_EndsWhenTerminal()
    {
        var domain = new TestRunDomain();
        var attempt = domain.Schedule();
        TestRunDomain.Pick(domain.AttemptService.MarkInProgress(attempt.Id));
        var live = new ScriptedLiveLogs(null, "--- olve-init ---\nfetch\n", "a\n", "a\nb\n");
        // Unreachable storage: the terminal read fails and the stream still ends.
        var s3 = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", MaxErrorRetry = 0 });
        var streamer = new AttemptLogStreamer(domain.AttemptService, live,
            new JobLogService(s3, new StorageOptions("b"), domain.AttemptService, live), new PodChangeSignals(TimeProvider.System), TimeProvider.System)
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
        };

        var events = new List<(string Type, string Data)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var item in streamer.StreamAsync(attempt.Id, cts.Token))
        {
            events.Add((item.Data.EventType, JsonSerializer.Serialize(item.Data, Contract.PipelinesJsonContext.Default.LogEvent)));
            if (events.Count(e => e.Type == "log") == 4 && item.Data.EventType == "log")
                await Assert.That(domain.AttemptService.MarkFailed(attempt.Id, "boom")).Succeeded();
        }

        await Assert.That(string.Join("|", events.Select(e => e.Data))).IsEqualTo(string.Join("|", new[]
        {
            """{"type":"log","text":"","reset":true}""",
            """{"type":"log","text":"--- olve-init ---\nfetch\n","reset":false}""",
            """{"type":"log","text":"a\n","reset":true}""",
            """{"type":"log","text":"b\n","reset":false}""",
            """{"type":"end","status":"failed"}""",
        }));
    }
}
