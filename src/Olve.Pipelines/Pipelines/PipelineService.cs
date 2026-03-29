using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines;

public class PipelineService(EntityStore<Pipeline> store)
{
    public Pipeline Create(string name)
    {
        var pipeline = new Pipeline(Id.New<Pipeline>(), name);
        store.Set(pipeline);
        return pipeline;
    }

    public bool TryGet(Id<Pipeline> id, [NotNullWhen(true)] out Pipeline? pipeline) => store.TryGet(id, out pipeline);

    public IReadOnlyList<Pipeline> List() => store.List();

    public bool Delete(Id<Pipeline> id) => store.Delete(id);
}
