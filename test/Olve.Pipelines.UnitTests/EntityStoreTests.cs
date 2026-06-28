using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Shared;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.UnitTests;

public class EntityStoreTests
{
    private record Counter(Id<Counter> Id, int Value) : IHasId<Id<Counter>>;

    [Test]
    public async Task Mutate_Mutates_FiresOnUpdatedOnce()
    {
        var store = new EntityStore<Counter>([]);
        var id = Id.New<Counter>();
        store.Set(new Counter(id, 0));

        var fires = 0;
        store.OnUpdated.Subscribe(_ => fires++);

        var result = store.Mutate(id, c => c with { Value = c.Value + 1 });

        await Assert.That(result).Succeeded();
        await Assert.That(fires).IsEqualTo(1);
        store.TryGet(id, out var stored);
        await Assert.That(stored!.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Mutate_NoOp_DoesNotFire()
    {
        var store = new EntityStore<Counter>([]);
        var id = Id.New<Counter>();
        store.Set(new Counter(id, 5));

        var fires = 0;
        store.OnUpdated.Subscribe(_ => fires++);

        // Returns an equal record — present but unchanged.
        var result = store.Mutate(id, c => c with { Value = 5 });

        await Assert.That(result).Succeeded();
        await Assert.That(fires).IsEqualTo(0);
    }

    [Test]
    public async Task Mutate_Missing_Fails_NoFire()
    {
        var store = new EntityStore<Counter>([]);

        var fires = 0;
        store.OnUpdated.Subscribe(_ => fires++);

        var result = store.Mutate(Id.New<Counter>(), c => c with { Value = c.Value + 1 });

        await Assert.That(result).Failed();
        await Assert.That(fires).IsEqualTo(0);
    }

    [Test]
    public async Task Mutate_ConcurrentIncrements_NoLostUpdates()
    {
        const int threads = 16;
        const int incrementsPerThread = 200;

        var store = new EntityStore<Counter>([]);
        var id = Id.New<Counter>();
        store.Set(new Counter(id, 0));

        using var start = new Barrier(threads);
        var tasks = Enumerable.Range(0, threads).Select(_t => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < incrementsPerThread; i++)
            {
                // Under heavy same-key contention a single Mutate may exhaust its bounded CAS
                // attempts; the contract is that the caller retries. Looping here proves no update
                // is ever lost (the regression target), independent of the attempt cap.
                while (store.Mutate(id, c => c with { Value = c.Value + 1 }).TryPickProblems(out _))
                {
                }
            }
        }));

        await Task.WhenAll(tasks);

        store.TryGet(id, out var result);
        await Assert.That(result!.Value).IsEqualTo(threads * incrementsPerThread);
    }

    [Test]
    public async Task ArtifactBundleService_UpdateStatus_UnknownId_Fails()
    {
        var service = new ArtifactBundleService(new EntityStore<ArtifactBundle>([]));

        var result = service.UpdateStatus(Id.New<ArtifactBundle>(), ArtifactBundleStatus.Completed);

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task ArtifactBundleService_UpdateStatus_KnownId_FlipsStatus()
    {
        var store = new EntityStore<ArtifactBundle>([]);
        var service = new ArtifactBundleService(store);
        var bundle = service.Create(Id.New<Pipeline>(), ArtifactBundleStatus.Pending);

        var result = service.UpdateStatus(bundle.Id, ArtifactBundleStatus.Completed);

        await Assert.That(result).Succeeded();
        store.TryGet(bundle.Id, out var updated);
        await Assert.That(updated!.Status).IsEqualTo(ArtifactBundleStatus.Completed);
    }
}
