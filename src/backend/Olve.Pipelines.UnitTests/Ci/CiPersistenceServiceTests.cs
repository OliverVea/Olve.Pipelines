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
        return (new CiPersistenceService(pulls, new EntityStore<CiRun>([]), new EntityStore<CiAttempt>([]), new StorageOptions("bucket"), readiness, NullLogger<CiPersistenceService>.Instance, store), pulls, readiness);
    }

    [Test]
    public async Task Tracked_pull_requests_survive_a_restart()
    {
        var snapshots = new MemorySnapshotStore();
        var (writer, writerStore, _) = Create(snapshots);
        await writer.StartingAsync(default);
        var pipelineId = Id.New<Pipeline>();
        var pulls = new PullRequestService(writerStore, new IdProvider());
        pulls.Observe(pipelineId, "main", Pull(number: 3, draft: true, mergeable: false), DateTimeOffset.UnixEpoch);
        pulls.Observe(pipelineId, "main", Pull(number: 4, state: "closed", merged: true), DateTimeOffset.UnixEpoch);
        await writer.SaveIfDirtyAsync();

        var (reader, readerStore, readiness) = Create(snapshots);
        await reader.StartingAsync(default);

        await Assert.That(readerStore.List().OrderBy(p => p.Number)).IsEquivalentTo(writerStore.List().OrderBy(p => p.Number));
        await Assert.That(readiness.IsReady).IsTrue();
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
