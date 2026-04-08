using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Triggers;

public class TriggerService(
    EntityStore<Trigger> store,
    IdProvider idProvider)
{
    private readonly EntityStoreIndex<Trigger, Id<Pipeline>> _byPipeline = store.CreateIndex(t => t.PipelineId);

    public Result<Trigger> Create(Id<Pipeline> pipelineId, string name, TriggerTarget target)
    {
        var trigger = new Trigger(
            idProvider.Create<Trigger>(),
            pipelineId,
            name,
            target,
            idProvider.CreateSecret(),
            DateTimeOffset.UtcNow);

        store.Set(trigger);
        return trigger;
    }

    public Result<Trigger> TryGet(Id<Trigger> id)
        => store.TryGet(id, out var trigger)
            ? trigger
            : Result.Failure<Trigger>(new ResultProblem($"Trigger '{id}' not found."));

    public Result<Trigger[]> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<Trigger>(ids.Count);
        foreach (var id in ids)
        {
            if (store.TryGet(id, out var trigger))
                results.Add(trigger);
        }
        return results.ToArray();
    }

    public DeletionResult Delete(Id<Trigger> id) => store.Delete(id);
}
