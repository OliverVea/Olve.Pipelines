using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public class PipelineService(EntityStore<Pipeline> store, IdProvider idProvider)
{
    public Pipeline Create(string name)
    {
        var pipeline = new Pipeline(idProvider.Create<Pipeline>(), name);
        store.Set(pipeline);
        return pipeline;
    }

    public bool TryGet(Id<Pipeline> id, [NotNullWhen(true)] out Pipeline? pipeline) => store.TryGet(id, out pipeline);

    public IReadOnlyList<Pipeline> List() => store.List();

    public DeletionResult Delete(Id<Pipeline> id) => store.Delete(id);
}
