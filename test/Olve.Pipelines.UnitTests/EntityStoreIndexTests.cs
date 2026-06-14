using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class EntityStoreIndexTests
{
    private static ProductionStep Step(Id<Pipeline> pipelineId, string name = "step")
        => new(Id.New<ProductionStep>(), name, pipelineId);

    [Test]
    public async Task GetForKey_GroupsByKey()
    {
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateIndex(s => s.PipelineId);
        var pipelineA = Id.New<Pipeline>();
        var pipelineB = Id.New<Pipeline>();

        var a1 = Step(pipelineA);
        var a2 = Step(pipelineA);
        var b1 = Step(pipelineB);
        store.Set(a1);
        store.Set(a2);
        store.Set(b1);

        await Assert.That(index.GetForKey(pipelineA)).Contains(a1.Id);
        await Assert.That(index.GetForKey(pipelineA)).Contains(a2.Id);
        await Assert.That(index.GetForKey(pipelineA).Count).IsEqualTo(2);
        await Assert.That(index.GetForKey(pipelineB).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Delete_RemovesFromIndex_AndDropsEmptyKey()
    {
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateIndex(s => s.PipelineId);
        var pipelineId = Id.New<Pipeline>();

        var step = Step(pipelineId);
        store.Set(step);
        await Assert.That(index.ContainsKey(pipelineId)).IsTrue();

        store.Delete(step.Id);

        // Regression: the previous implementation never pruned on delete because the entity is
        // already gone from the store when OnDeleted fires.
        await Assert.That(index.GetForKey(pipelineId).Count).IsEqualTo(0);
        await Assert.That(index.ContainsKey(pipelineId)).IsFalse();
    }

    [Test]
    public async Task GetForKey_ReturnsStableSnapshot_UnaffectedByLaterAdd()
    {
        // Deterministic non-aliasing guard (design blocker B7): a reference handed out by
        // GetForKey must not reflect a subsequent mutation. Fails on the old live-HashSet code,
        // passes on the immutable-backed index.
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateIndex(s => s.PipelineId);
        var pipelineId = Id.New<Pipeline>();

        store.Set(Step(pipelineId));
        var snapshot = index.GetForKey(pipelineId);
        await Assert.That(snapshot.Count).IsEqualTo(1);

        store.Set(Step(pipelineId)); // mutates the index for the same key

        await Assert.That(snapshot.Count).IsEqualTo(1);           // captured reference unchanged
        await Assert.That(index.GetForKey(pipelineId).Count).IsEqualTo(2); // fresh read sees it
    }

    [Test]
    public async Task GetForKey_ReturnsStableSnapshot_UnaffectedByLaterDelete()
    {
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateIndex(s => s.PipelineId);
        var pipelineId = Id.New<Pipeline>();

        var a = Step(pipelineId);
        var b = Step(pipelineId);
        store.Set(a);
        store.Set(b);

        var snapshot = index.GetForKey(pipelineId);
        await Assert.That(snapshot.Count).IsEqualTo(2);

        store.Delete(a.Id);

        await Assert.That(snapshot.Count).IsEqualTo(2);           // captured reference unchanged
        await Assert.That(index.GetForKey(pipelineId).Count).IsEqualTo(1); // fresh read sees it
    }

    [Test]
    public async Task ConcurrentEnumerationAndMutation_DoesNotThrow()
    {
        // Supplementary stress test (B7): enumerate while concurrently adding/deleting on the
        // same key. The old live-HashSet code throws InvalidOperationException on concurrent Add
        // during iteration; the immutable index never does.
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateIndex(s => s.PipelineId);
        var pipelineId = Id.New<Pipeline>();

        var seed = Enumerable.Range(0, 50).Select(_ => Step(pipelineId)).ToArray();
        foreach (var s in seed) store.Set(s);

        using var cts = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var total = 0;
                foreach (var id in index.GetForKey(pipelineId)) total += id.GetHashCode();
                _ = total;
            }
        });

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var s = Step(pipelineId);
                store.Set(s);
                store.Delete(s.Id);
            }
        });

        await writer;
        cts.Cancel();
        await reader;

        // No exception means the test passed; assert the index is back to the seeded state.
        await Assert.That(index.GetForKey(pipelineId).Count).IsEqualTo(seed.Length);
    }
}
