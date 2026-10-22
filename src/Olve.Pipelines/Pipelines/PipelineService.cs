using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public class PipelineService(EntityStore<Pipeline> store, IdProvider idProvider)
{
    public Result<Pipeline> Create(string name)
    {
        var pipeline = new Pipeline(idProvider.Create<Pipeline>(), name);
        store.Set(pipeline);
        return pipeline;
    }

    /// <summary>Sets name and description from the active version's manifest (no-op when unchanged).</summary>
    public Result<Pipeline> SetDefinition(Id<Pipeline> id, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ResultProblem("Pipeline name is required.");
        if (!store.TryGet(id, out var pipeline))
            return new NotFoundProblem("Pipeline '{0}' not found.", id);
        if (pipeline.Name == name && pipeline.Description == description)
            return pipeline;

        var updated = pipeline with { Name = name, Description = description };
        store.Set(updated);
        return updated;
    }

    public bool TryGet(Id<Pipeline> id, [NotNullWhen(true)] out Pipeline? pipeline) => store.TryGet(id, out pipeline);

    public IReadOnlyList<Pipeline> List() => store.List();

    public DeletionResult Delete(Id<Pipeline> id) => store.Delete(id);
}
