using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Shared.Persistence;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;
using static Olve.Pipelines.UnitTests.Ci.PullRequestFixtures;

namespace Olve.Pipelines.UnitTests.Ci;

public class CiPersistenceServiceTests
{
    private static (CiPersistenceService Service, EntityStore<PullRequest> Store, PersistenceReadiness Readiness) Create(ISnapshotStore store)
    {
        var pulls = new EntityStore<PullRequest>([]);
        var readiness = new PersistenceReadiness();
        return (new CiPersistenceService(pulls, new EntityStore<CiRun>([]), new EntityStore<CiAttempt>([]), new EntityStore<CiBuild>([]), new StorageOptions("bucket"), readiness, NullLogger<CiPersistenceService>.Instance, store), pulls, readiness);
    }

    [Test]
    public async Task Tracked_pull_requests_survive_a_restart()
    {
        var snapshots = new MemorySnapshotStore();
        var (writer, writerStore, _) = Create(snapshots);
        await writer.StartingAsync(default);
        var pipelineId = Id.New<Pipeline>();
        var pulls = new PullRequestService(writerStore, new IdProvider(), PullRequestFixtures.AllowedAuthors);
        pulls.Observe(pipelineId, "main", Pull(number: 3, draft: true, mergeable: false), DateTimeOffset.UnixEpoch);
        pulls.Observe(pipelineId, "main", Pull(number: 4, state: "closed", merged: true), DateTimeOffset.UnixEpoch);
        await writer.SaveIfDirtyAsync();

        var (reader, readerStore, readiness) = Create(snapshots);
        await reader.StartingAsync(default);

        await Assert.That(readerStore.List().OrderBy(p => p.Number)).IsEquivalentTo(writerStore.List().OrderBy(p => p.Number));
        await Assert.That(readiness.IsReady).IsTrue();
    }

    // Catches: a restart forgetting a pull request's own build, so its runs rebuild it or never settle.
    [Test]
    public async Task Builds_of_pull_requests_own_definitions_survive_a_restart()
    {
        var snapshots = new MemorySnapshotStore();
        CiPersistenceService Service(EntityStore<CiBuild> builds) => new(new EntityStore<PullRequest>([]), new EntityStore<CiRun>([]),
            new EntityStore<CiAttempt>([]), builds, new StorageOptions("bucket"), new PersistenceReadiness(), NullLogger<CiPersistenceService>.Instance, snapshots);
        var written = new EntityStore<CiBuild>([]);
        var writer = Service(written);
        await writer.StartingAsync(default);
        var service = new CiBuildService(written, new IdProvider(), TimeProvider.System);
        var pipelineId = Id.New<Pipeline>();
        service.Finish(service.Start(pipelineId, new string('a', 40)), "{\"apiVersion\":\"2.0\"}", null);
        service.Finish(service.Start(pipelineId, new string('b', 40)), null, ["Build/describe failed: the build failed (exit 1)."]);
        service.Start(pipelineId, new string('c', 40));
        await writer.SaveIfDirtyAsync();

        var read = new EntityStore<CiBuild>([]);
        await Service(read).StartingAsync(default);

        await Assert.That(read.List().OrderBy(b => b.CommitSha).Select(b => (b.CommitSha[0], b.Status, b.Manifest, string.Join("|", b.Problems ?? []))))
            .IsEquivalentTo(new[]
            {
                ('a', CiBuildStatus.Succeeded, (string?)"{\"apiVersion\":\"2.0\"}", ""),
                ('b', CiBuildStatus.Failed, null, "Build/describe failed: the build failed (exit 1)."),
                ('c', CiBuildStatus.Building, null, ""),
            });
    }

    // Catches: a restart forgetting the queue's order, an entry mid-test, or a split batch's limit (bisect would start over).
    [Test]
    public async Task The_merge_queue_and_its_batch_limit_survive_a_restart()
    {
        var snapshots = new MemorySnapshotStore();
        var pulls = new EntityStore<PullRequest>([]);
        var tracked = new PullRequestService(pulls, new IdProvider(), PullRequestFixtures.AllowedAuthors);
        var pipelineId = Id.New<Pipeline>();
        tracked.Observe(pipelineId, "main", Pull(number: 3), DateTimeOffset.UnixEpoch);
        tracked.Observe(pipelineId, "main", Pull(number: 4), DateTimeOffset.UnixEpoch);
        CiPersistenceService Service(EntityStore<CiQueueEntry> entries, CiQueueService queue) => new(new EntityStore<PullRequest>([]), new EntityStore<CiRun>([]),
            new EntityStore<CiAttempt>([]), new EntityStore<CiBuild>([]), new StorageOptions("bucket"), new PersistenceReadiness(),
            NullLogger<CiPersistenceService>.Instance, snapshots, entries, queue);
        var written = new EntityStore<CiQueueEntry>([]);
        var writtenQueue = new CiQueueService(written, new IdProvider(), TimeProvider.System);
        var writer = Service(written, writtenQueue);
        await writer.StartingAsync(default);
        tracked.TryGet(pipelineId, 3, out var three);
        tracked.TryGet(pipelineId, 4, out var four);
        writtenQueue.Testing(TestRunDomain.Pick(writtenQueue.Enqueue(three!, CiQueueVia.Api, "oliver")), 12);
        TestRunDomain.Pick(writtenQueue.Enqueue(four!, CiQueueVia.AutoMerge, null));
        writtenQueue.Split(pipelineId, 3);
        await writer.SaveIfDirtyAsync();

        var read = new EntityStore<CiQueueEntry>([]);
        var readQueue = new CiQueueService(read, new IdProvider(), TimeProvider.System);
        await Service(read, readQueue).StartingAsync(default);

        await Assert.That(readQueue.Unfinished(pipelineId)).IsEquivalentTo(writtenQueue.Unfinished(pipelineId), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(readQueue.Limit(pipelineId)).IsEqualTo(new CiQueueLimit(pipelineId, 2, 3));
    }

    // Catches: a restart forgetting a preview, so its pull request closing would leave it up for good.
    [Test]
    public async Task Previews_survive_a_restart()
    {
        var snapshots = new MemorySnapshotStore();
        CiPersistenceService Service(EntityStore<Preview> previews) => new(new EntityStore<PullRequest>([]), new EntityStore<CiRun>([]),
            new EntityStore<CiAttempt>([]), new EntityStore<CiBuild>([]), new StorageOptions("bucket"), new PersistenceReadiness(),
            NullLogger<CiPersistenceService>.Instance, snapshots, previews: previews);
        var written = new EntityStore<Preview>([]);
        var writer = Service(written);
        await writer.StartingAsync(default);
        var preview = new Preview(Id.New<Preview>(), Id.New<Pipeline>(), 7, PreviewState.Failing, Id.New<CiRun>(), 12, DateTimeOffset.UnixEpoch,
            "https://pr-7.example", Id.New<CiRun>(), 2, DateTimeOffset.UnixEpoch.AddMinutes(5));
        written.Set(preview);
        await writer.SaveIfDirtyAsync();

        var read = new EntityStore<Preview>([]);
        await Service(read).StartingAsync(default);

        await Assert.That(read.List()).IsEquivalentTo([preview]);
    }

    [Test]
    public async Task A_snapshot_that_cant_be_read_fails_startup_without_overwriting_it()
    {
        var snapshots = new MemorySnapshotStore();
        snapshots.Objects[CiPersistenceService.Key] = "{ not json"u8.ToArray();
        var (service, _, readiness) = Create(snapshots);

        Exception? thrown = null;
        try { await service.StartingAsync(default); }
        catch (Exception ex) { thrown = ex; }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(snapshots.Objects[CiPersistenceService.Key]).IsEquivalentTo("{ not json"u8.ToArray());
        await Assert.That(readiness.IsReady).IsFalse();
    }
}
