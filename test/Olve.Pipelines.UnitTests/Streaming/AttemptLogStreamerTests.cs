using Olve.Pipelines.Kubernetes.Harness;
using Amazon.Runtime;
using Amazon.S3;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Streaming;
using Olve.Results;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Streaming;

public class AttemptLogStreamerTests
{
    private sealed class ScriptedLiveLogs(params string?[] snapshots) : IAttemptLiveLogs
    {
        private int _next;
        public Task<string?> TryGetAsync(Attempt attempt, CancellationToken ct)
            => Task.FromResult(snapshots[Math.Min(_next++, snapshots.Length - 1)]);
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
            events.Add((item.EventType, item.Data));
            if (events.Count(e => e.Type == "log") == 4 && item.EventType == "log")
                await Assert.That(domain.AttemptService.MarkFailed(attempt.Id, "boom")).Succeeded();
        }

        await Assert.That(string.Join("|", events.Select(e => e.Data))).IsEqualTo(string.Join("|", new[]
        {
            """{"text":"","reset":true}""",
            """{"text":"--- olve-init ---\nfetch\n","reset":false}""",
            """{"text":"a\n","reset":true}""",
            """{"text":"b\n","reset":false}""",
            """{"status":"failed"}""",
        }));
    }
}
