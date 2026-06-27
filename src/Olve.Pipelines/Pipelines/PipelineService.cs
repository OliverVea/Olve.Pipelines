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

    /// <summary>
    /// Sets the pipeline's name. The name is GitOps config (driven by <c>.pipelines/config.yaml</c>
    /// via reconcile), not binding identity — the bind only seeds a provisional name. A no-op when
    /// the name already matches, so an idempotent reconcile fires no spurious update event.
    /// </summary>
    public Result<Pipeline> SetName(Id<Pipeline> id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ResultProblem("Pipeline name is required.");

        if (!store.TryGet(id, out var pipeline))
            return new ResultProblem($"Pipeline '{id}' not found.");

        if (pipeline.Name == name)
            return pipeline;

        var renamed = pipeline with { Name = name };
        store.Set(renamed);
        return renamed;
    }

    public bool TryGet(Id<Pipeline> id, [NotNullWhen(true)] out Pipeline? pipeline) => store.TryGet(id, out pipeline);

    public IReadOnlyList<Pipeline> List() => store.List();

    public DeletionResult Delete(Id<Pipeline> id) => store.Delete(id);
}
