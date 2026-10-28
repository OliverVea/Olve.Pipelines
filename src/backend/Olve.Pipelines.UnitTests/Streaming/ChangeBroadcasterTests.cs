using System.Net.ServerSentEvents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Streaming;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Streaming;

public class ChangeBroadcasterTests
{
    private static ChangeBroadcaster New() => new(TimeProvider.System, NullLogger<ChangeBroadcaster>.Instance);

    private static ChangeEvent Change(Id<Pipeline> pipelineId, string type = "run") => new(type, pipelineId, "{}");

    private static List<SseItem<string>> Drain(ChangeSubscription subscription)
    {
        var items = new List<SseItem<string>>();
        while (subscription.Reader.TryRead(out var item)) items.Add(item);
        return items;
    }

    [Test]
    public async Task Publish_FansOutToEveryMatchingSubscriber()
    {
        var broadcaster = New();
        var p1 = Id.New<Pipeline>();
        var p2 = Id.New<Pipeline>();
        using var all = broadcaster.Subscribe(null, null);
        using var onlyP1 = broadcaster.Subscribe(p1, null);

        broadcaster.Publish(Change(p1));
        broadcaster.Publish(Change(p2, "attempt"));

        await Assert.That(Drain(all).Select(i => i.EventType)).IsEquivalentTo(["run", "attempt"]);
        var filtered = Drain(onlyP1);
        await Assert.That(filtered.Count).IsEqualTo(1);
        await Assert.That(filtered[0].EventId).EndsWith("-1");
    }

    [Test]
    public async Task SlowClient_IsDisconnected_OthersKeepReceiving()
    {
        var broadcaster = New();
        var p = Id.New<Pipeline>();
        using var slow = broadcaster.Subscribe(null, null);
        using var fast = broadcaster.Subscribe(null, null);

        for (var i = 0; i < ChangeBroadcaster.ClientCapacity; i++)
        {
            broadcaster.Publish(Change(p));
            Drain(fast);
        }
        broadcaster.Publish(Change(p));

        await Assert.That(Drain(fast).Count).IsEqualTo(1);
        await Assert.That(Drain(slow).Count).IsEqualTo(ChangeBroadcaster.ClientCapacity);
        await Assert.That(slow.Reader.Completion.IsCompleted).IsTrue();
    }

    [Test]
    public async Task Dispose_Unsubscribes()
    {
        var broadcaster = New();
        var subscription = broadcaster.Subscribe(null, null);
        await Assert.That(broadcaster.SubscriberCount).IsEqualTo(1);

        subscription.Dispose();

        await Assert.That(broadcaster.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancellingTheStream_Unsubscribes()
    {
        var broadcaster = New();
        using var cts = new CancellationTokenSource();
        await using var stream = broadcaster.StreamAsync(null, null, cts.Token).GetAsyncEnumerator(cts.Token);

        await Assert.That(await stream.MoveNextAsync()).IsTrue();
        await Assert.That(stream.Current.EventType).IsEqualTo("hello");
        await Assert.That(broadcaster.SubscriberCount).IsEqualTo(1);

        var next = stream.MoveNextAsync();
        await cts.CancelAsync();

        await Assert.That(await next).IsFalse();
        await Assert.That(broadcaster.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task LastEventId_Current_NeedsNoResync_StaleOrForeign_Does()
    {
        var broadcaster = New();
        var p = Id.New<Pipeline>();
        broadcaster.Publish(Change(p));
        string current;
        using (var first = broadcaster.Subscribe(null, null)) current = first.CurrentId;

        using var upToDate = broadcaster.Subscribe(null, current);
        broadcaster.Publish(Change(p));
        using var stale = broadcaster.Subscribe(null, current);
        using var otherBoot = broadcaster.Subscribe(null, "1-999");
        using var fresh = broadcaster.Subscribe(null, null);

        await Assert.That(upToDate.NeedsResync).IsFalse();
        await Assert.That(stale.NeedsResync).IsTrue();
        await Assert.That(otherBoot.NeedsResync).IsTrue();
        await Assert.That(fresh.NeedsResync).IsFalse();
    }

    [Test]
    public async Task FilteredClient_IgnoresEventsOfOtherPipelines_ForResync()
    {
        var broadcaster = New();
        var mine = Id.New<Pipeline>();
        broadcaster.Publish(Change(mine));
        string seen;
        using (var s = broadcaster.Subscribe(mine, null)) seen = s.CurrentId;

        broadcaster.Publish(Change(Id.New<Pipeline>()));
        using var reconnect = broadcaster.Subscribe(mine, seen);

        await Assert.That(reconnect.NeedsResync).IsFalse();
    }

    [Test]
    public async Task Stream_StartsWithHelloAndRetry_ThenResyncWhenStale()
    {
        var broadcaster = New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = broadcaster.StreamAsync(null, "1-1", cts.Token).GetAsyncEnumerator(cts.Token);

        await stream.MoveNextAsync();
        await Assert.That(stream.Current.EventType).IsEqualTo("hello");
        await Assert.That(stream.Current.ReconnectionInterval).IsNotNull();
        await stream.MoveNextAsync();
        await Assert.That(stream.Current.EventType).IsEqualTo("resync");
    }

    [Test]
    public async Task Heartbeat_IsSentWhileIdle()
    {
        var broadcaster = New();
        using var subscription = broadcaster.Subscribe(null, null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = SseStreams.WithHeartbeat(subscription.Reader, TimeProvider.System, TimeSpan.FromMilliseconds(20), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        await stream.MoveNextAsync();

        await Assert.That(stream.Current.EventType).IsEqualTo("ping");
    }

    [Test]
    public async Task DomainChanges_ReachSubscribers_ThroughProductionWiring()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        await using var provider = services.BuildServiceProvider();
        foreach (var startup in provider.GetServices<IRunOnStartup>())
            if (startup.Run().TryPickProblems(out var problems)) throw new InvalidOperationException(problems.First().ToString());
        using var subscription = provider.GetRequiredService<ChangeBroadcaster>().Subscribe(null, null);

        var pipeline = TestRunDomain.Pick(provider.GetRequiredService<PipelineService>().Create("p"));
        var version = TestRunDomain.Pick(provider.GetRequiredService<PipelineVersionService>().Add(
            pipeline.Id, """{"apiVersion":"2.0"}""", "sha256:a", "c0ffee",
            new Dictionary<string, int> { ["build"] = 1 }, new Dictionary<string, int>()));
        TestRunDomain.Pick(provider.GetRequiredService<PipelineActivationService>().BeginSwitch(pipeline.Id, version.Id));
        var run = TestRunDomain.Pick(provider.GetRequiredService<RunService>().Create(
            pipeline.Id, version.Id, "{}", new RunTrigger(RunTriggerKind.Manual)));
        TestRunDomain.Pick(provider.GetRequiredService<AttemptService>().Schedule(run.Id, "build"));

        var items = Drain(subscription);
        var types = items.Select(i => i.EventType).ToList();
        await Assert.That(types).Contains("pipeline");
        await Assert.That(types).Contains("version");
        await Assert.That(types).Contains("run");
        await Assert.That(types).Contains("attempt");
        var attempt = items.First(i => i.EventType == "attempt").Data;
        await Assert.That(attempt).Contains("\"step\":\"build\"");
        await Assert.That(attempt).Contains($"\"pipelineId\":\"{pipeline.Id.Value.Value}\"");
    }

    [Test]
    public async Task ConfigPendingQueuedPollSecretsAndVersionDeletes_ReachSubscribers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPipelineServices();
        await using var provider = services.BuildServiceProvider();
        foreach (var startup in provider.GetServices<IRunOnStartup>())
            if (startup.Run().TryPickProblems(out var problems)) throw new InvalidOperationException(problems.First().ToString());

        var pipeline = TestRunDomain.Pick(provider.GetRequiredService<PipelineService>().Create("p"));
        var version = TestRunDomain.Pick(provider.GetRequiredService<PipelineVersionService>().Add(
            pipeline.Id, """{"apiVersion":"2.0"}""", "sha256:a", "c0ffee",
            new Dictionary<string, int> { ["build"] = 1 }, new Dictionary<string, int>()));
        using var subscription = provider.GetRequiredService<ChangeBroadcaster>().Subscribe(null, null);

        TestRunDomain.Pick(provider.GetRequiredService<ConfigSnapshotService>().Add(pipeline.Id, "a: 1\n", "test"));
        var pending = provider.GetRequiredService<AttachmentStore<Pipeline, PendingVersion>>();
        pending.Set(pipeline.Id, new PendingVersion(version.Id, version.Number, DateTimeOffset.UtcNow, ["secret X not set"], []));
        pending.Remove(pipeline.Id);
        var queued = provider.GetRequiredService<AttachmentStore<Pipeline, PendingRunStart>>();
        queued.Set(pipeline.Id, new PendingRunStart("{}", new RunTrigger(RunTriggerKind.Manual), DateTimeOffset.UtcNow));
        provider.GetRequiredService<AttachmentStore<Pipeline, PollTriggerStates>>().Set(pipeline.Id,
            new PollTriggerStates(new Dictionary<string, PollTriggerState> { ["poll"] = new("https://x", "v1", DateTimeOffset.UtcNow) }));
        provider.GetRequiredService<PipelineSecretEvents>().OnChanged.Invoke(pipeline.Id);
        await Assert.That(provider.GetRequiredService<PipelineVersionService>().Delete(version.Id).Succeeded).IsTrue();

        var items = Drain(subscription).Select(i => $"{i.EventType} {i.Data}").ToList();
        await Assert.That(items).Contains(i => i.StartsWith("config ") && i.Contains("\"snapshot\":1"));
        await Assert.That(items).Contains(i => i.StartsWith("pending ") && i.Contains($"\"version\":{version.Number}"));
        await Assert.That(items).Contains(i => i.StartsWith("pending ") && i.Contains("\"version\":null"));
        await Assert.That(items).Contains(i => i.StartsWith("queued ") && i.Contains("\"queued\":true"));
        await Assert.That(items).Contains(i => i.StartsWith("poll "));
        await Assert.That(items).Contains(i => i.StartsWith("secrets "));
        await Assert.That(items).Contains(i => i.StartsWith("version ") && i.Contains("\"change\":\"deleted\""));
    }
}
