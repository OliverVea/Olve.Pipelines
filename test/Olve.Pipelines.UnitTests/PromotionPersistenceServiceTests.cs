using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class PromotionPersistenceServiceTests
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

    private static (PromotionPersistenceService Service, AttachmentStore<ProcessingStep, ProcessingStepPromotion> Promotions, PersistenceReadiness Readiness)
        CreateService(ISnapshotStore? store, StorageMode mode = StorageMode.Persistent)
    {
        var steps = new EntityStore<ProcessingStep>([]);
        var promotions = new AttachmentStore<ProcessingStep, ProcessingStepPromotion>(steps);
        var readiness = new PersistenceReadiness();

        var service = new PromotionPersistenceService(
            promotions,
            new StorageOptions("test-bucket", Mode: mode),
            readiness,
            NullLogger<PromotionPersistenceService>.Instance,
            store);

        return (service, promotions, readiness);
    }

    [Test]
    public async Task LoadThrows_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeSnapshotStore { ReadException = new InvalidOperationException("STS auth failed") };
        var (service, _, readiness) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task CorruptSnapshot_DoesNotSave_AndFailsStartup()
    {
        var store = new FakeSnapshotStore { Data = "{ not valid json"u8.ToArray() };
        var (service, _, readiness) = CreateService(store);

        await Assert.That(async () => await service.StartingAsync(CancellationToken.None))
            .Throws<JsonException>();

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsFalse();
    }

    [Test]
    public async Task EphemeralMode_DoesNotPersist_ButMarksReady()
    {
        var store = new FakeSnapshotStore();
        var (service, _, readiness) = CreateService(store, StorageMode.Ephemeral);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(0);
        await Assert.That(readiness.IsReady).IsTrue();
    }

    [Test]
    public async Task BlockedSet_RoundTrips_AcrossReload()
    {
        var blockedStep = Id.New<ProcessingStep>();
        var enabledStep = Id.New<ProcessingStep>();

        // First instance: block one step, leave another enabled, then persist on shutdown.
        var store = new FakeSnapshotStore();
        var (service, promotions, _) = CreateService(store);
        await service.StartingAsync(CancellationToken.None);
        promotions.Set(blockedStep, new ProcessingStepPromotion(true));
        promotions.Set(enabledStep, new ProcessingStepPromotion(false));
        await service.StoppingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsGreaterThan(0);

        // Second instance over the same store: only the blocked step should be restored.
        var (service2, promotions2, _) = CreateService(store);
        await service2.StartingAsync(CancellationToken.None);

        await Assert.That(promotions2.TryGet(blockedStep, out var restored)).IsTrue();
        await Assert.That(restored!.Blocked).IsTrue();
        await Assert.That(promotions2.TryGet(enabledStep, out _)).IsFalse();
    }

    [Test]
    public async Task FirstRun_WritesEmptyBaseline_AndMarksReady()
    {
        var store = new FakeSnapshotStore { Data = null };
        var (service, _, readiness) = CreateService(store);

        await service.StartingAsync(CancellationToken.None);

        await Assert.That(store.WriteCount).IsEqualTo(1);
        await Assert.That(readiness.IsReady).IsTrue();
    }
}
