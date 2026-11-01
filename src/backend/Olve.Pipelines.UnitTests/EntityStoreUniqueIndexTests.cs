using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.UnitTests;

public class EntityStoreUniqueIndexTests
{
    private record TestStep(Id<TestStep> Id, string Name, Id<Pipeline> PipelineId) : IHasId<Id<TestStep>>;

    private static TestStep Step(string name)
        => new(Id.New<TestStep>(), name, Id.New<Pipeline>());

    [Test]
    public async Task TryGet_ResolvesKeyToId()
    {
        var store = new EntityStore<TestStep>([]);
        var index = store.CreateUniqueIndex(s => s.Name);

        var step = Step("build");
        store.Set(step);

        await Assert.That(index.TryGet("build", out var id)).IsTrue();
        await Assert.That(id).IsEqualTo(step.Id);
    }
}
