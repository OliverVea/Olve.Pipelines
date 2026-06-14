using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class EntityStoreUniqueIndexTests
{
    private static ProductionStep Step(string name)
        => new(Id.New<ProductionStep>(), name, Id.New<Pipeline>());

    [Test]
    public async Task TryGet_ResolvesKeyToId()
    {
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateUniqueIndex(s => s.Name);

        var step = Step("build");
        store.Set(step);

        await Assert.That(index.TryGet("build", out var id)).IsTrue();
        await Assert.That(id).IsEqualTo(step.Id);
    }

    [Test]
    public async Task Delete_RemovesFromIndex()
    {
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateUniqueIndex(s => s.Name);

        var step = Step("build");
        store.Set(step);
        await Assert.That(index.ContainsKey("build")).IsTrue();

        store.Delete(step.Id);

        // Regression: the previous implementation never pruned on delete because the entity is
        // already gone from the store when OnDeleted fires.
        await Assert.That(index.ContainsKey("build")).IsFalse();
        await Assert.That(index.TryGet("build", out _)).IsFalse();
    }

    [Test]
    public async Task Delete_OfReboundId_DoesNotDropTheLiveEntry()
    {
        // A key rebound to a different id (collision), then the original id is deleted: the forward
        // entry must survive because it now points at the newer id.
        var store = new EntityStore<ProductionStep>([]);
        var index = store.CreateUniqueIndex(s => s.Name);

        var first = Step("build");
        var second = Step("build"); // same key, different id
        store.Set(first);
        store.Set(second); // rebinds "build" -> second.Id

        store.Delete(first.Id);

        await Assert.That(index.TryGet("build", out var id)).IsTrue();
        await Assert.That(id).IsEqualTo(second.Id);
    }
}
