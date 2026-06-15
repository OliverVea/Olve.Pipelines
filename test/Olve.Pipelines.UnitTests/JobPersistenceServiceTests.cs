using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.UnitTests;

public class JobPersistenceServiceTests
{
    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public byte[]? Data;
        public Exception? ReadException;
        public int WriteCount;

        public Task<byte[]?> TryReadAsync(string key, CancellationToken ct)
        {
            if (ReadException is not null) return Task.FromException<byte[]?>(ReadException);
            return Task.FromResult(Data);
        }

        public Task WriteAsync(string key, byte[] content, CancellationToken ct)
        {
            WriteCount++;
            Data = content;
            return Task.CompletedTask;
        }
    }

    private static (JobPersistenceService Service, PersistenceReadiness Readiness)
        CreateService(ISnapshotStore? store, StorageMode mode = StorageMode.Persistent)
    {
        var jobs = new EntityStore<Job>([]);
        var jobGroups = new EntityStore<JobGroup>([]);
        var readiness = new PersistenceReadiness();

        var service = new JobPersistenceService(
            jobs, jobGroups,
            new StorageOptions("test-bucket", Mode: mode),
            readiness,
            NullLogger<JobPersistenceService>.Instance,
            store);

        return (service, readiness);
    }

    private static byte[] SerializeSnapshot(JobSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, JobPersistenceJsonContext.Default.JobSnapshot);

    // The regression: a non-404 load failure must NOT trigger a save (which would overwrite good state).
    [Test]
    public async Task LoadThrows_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeSnapshotStore { ReadException = new InvalidOperationException("STS auth failed") };
        var (service, readiness) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task CorruptSnapshot_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeSnapshotStore { Data = "{ not valid json"u8.ToArray() };
        var (service, readiness) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<JsonException>();

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task FirstRun_SavesEmptyBaseline_AndBecomesReady()
    {
        var store = new FakeSnapshotStore { Data = null };
        var (service, readiness) = CreateService(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(1);
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task SuccessfulLoad_NoRedundantSave_AndBecomesReady()
    {
        var store = new FakeSnapshotStore { Data = SerializeSnapshot(new JobSnapshot([], [])) };
        var (service, readiness) = CreateService(store);

        await Assert.That(readiness.IsReady).IsFalse();

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task EphemeralMode_NoStore_BecomesReadyImmediately()
    {
        var (service, readiness) = CreateService(store: null, mode: StorageMode.Ephemeral);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task PersistentMode_WithNoStore_FailsStartup()
    {
        var (service, readiness) = CreateService(store: null, mode: StorageMode.Persistent);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(readiness.IsReady).IsFalse();
    }
}
