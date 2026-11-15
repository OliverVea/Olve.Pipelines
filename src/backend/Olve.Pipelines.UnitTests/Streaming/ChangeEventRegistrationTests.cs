using Olve.Pipelines.UnitTests.Ci;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Ci;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Streaming;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Streaming;

/// <summary>
/// Domain changes reach <c>/api/events</c> as the notices <c>pl wait</c> and the UI wake on. A change with no
/// notice leaves a waiter asleep until its one-minute fallback.
/// </summary>
public class ChangeEventRegistrationTests
{
    private sealed record Wired(SchedulerScenario S, PipelineSecretEvents Secrets, PullRequestService Pulls, CiQueueService Queue, ChangeSubscription Subscription);

    private static Wired Wire()
    {
        var s = new SchedulerScenario();
        var d = s.D;
        var secrets = new PipelineSecretEvents();
        var pullStore = new EntityStore<PullRequest>([]);
        var pullEvents = new PullRequestEvents();
        pullStore.OnAdded.Subscribe(pullEvents.OnAdded.Invoke);
        pullStore.OnUpdated.Subscribe(pullEvents.OnUpdated.Invoke);
        pullStore.OnDeleted.Subscribe(pullEvents.OnDeleted.Invoke);
        var queueStore = new EntityStore<CiQueueEntry>([]);
        var queueEvents = new CiQueueEvents();
        queueStore.OnAdded.Subscribe(queueEvents.OnAdded.Invoke);
        queueStore.OnUpdated.Subscribe(queueEvents.OnUpdated.Invoke);
        queueStore.OnDeleted.Subscribe(queueEvents.OnDeleted.Invoke);
        var broadcaster = new ChangeBroadcaster(d.Time, NullLogger<ChangeBroadcaster>.Instance);
        var registration = new ChangeEventRegistration(
            d.PipelineEvents, d.RunEvents, d.AttemptEvents, d.VersionEvents, new PipelineConfigBindingEvents(),
            d.Activations, d.BlockedSteps, d.PendingVersions, d.PendingStarts, d.PollTriggers, d.ConfigSnapshotStore,
            secrets, pullEvents, new CiRunEvents(), new CiAttemptEvents(), queueEvents, broadcaster);
        if (registration.Run().TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems));
        return new Wired(s, secrets, new PullRequestService(pullStore, new IdProvider(), PullRequestFixtures.AllowedAuthors), new CiQueueService(queueStore, new IdProvider(), d.Time),
            broadcaster.Subscribe(s.Pipeline.Id, lastEventId: null));
    }

    private static List<string> Drain(ChangeSubscription subscription)
    {
        var types = new List<string>();
        while (subscription.Reader.TryRead(out var item))
            types.Add(item.Data.EventType);
        return types;
    }

    // Catches: `pl wait run` sleeping through a run's and its attempts' progress.
    [Test]
    public async Task StartingARun_NotifiesTheRunAndItsAttempts()
    {
        var w = Wire();

        await Assert.That(w.S.D.Starts.Start(w.S.Pipeline.Id, SchedulerScenario.Input(), new RunTrigger(RunTriggerKind.Manual))).Succeeded();

        var types = Drain(w.Subscription);
        await Assert.That(types).Contains("run");
        await Assert.That(types).Contains("attempt");
    }

    [Test]
    public async Task PipelineStateChanges_EachNotify()
    {
        var w = Wire();
        var p = w.S.Pipeline.Id;
        var d = w.S.D;

        d.BlockedSteps.Set(p, new PipelineBlockedSteps(["deploy-prod"]));
        d.BlockedSteps.Remove(p);
        d.PendingStarts.Set(p, new PendingRunStart("{}", new RunTrigger(RunTriggerKind.Manual), DateTimeOffset.UnixEpoch));
        d.PendingStarts.Remove(p);
        d.PollTriggers.Set(p, new PollTriggerStates(new Dictionary<string, PollTriggerState>()));
        w.Secrets.OnChanged.Invoke(p);
        w.S.AddVersion();
        w.Pulls.Observe(p, "main", Ci.PullRequestFixtures.Pull(updatedAt: DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);
        w.Pulls.Observe(p, "main", Ci.PullRequestFixtures.Pull(title: "Renamed", updatedAt: DateTimeOffset.UnixEpoch.AddSeconds(1)), DateTimeOffset.UnixEpoch);
        w.Pulls.TryGet(p, 7, out var pull);
        TestRunDomain.Pick(w.Queue.Enqueue(pull!, CiQueueVia.Api, "oliver"));
        w.Pulls.Observe(p, "main", Ci.PullRequestFixtures.Pull(@base: "release", updatedAt: DateTimeOffset.UnixEpoch.AddSeconds(2)), DateTimeOffset.UnixEpoch);

        await Assert.That(Drain(w.Subscription)).IsEquivalentTo(
            ["blocked", "blocked", "queued", "queued", "poll", "secrets", "version", "pull", "pull", "ci-queue", "pull"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnotherPipelinesChanges_AreFilteredOut()
    {
        var w = Wire();

        w.S.D.BlockedSteps.Set(w.S.D.CreatePipeline().Id, new PipelineBlockedSteps(["x"]));

        await Assert.That(Drain(w.Subscription)).IsEmpty();
    }
}
