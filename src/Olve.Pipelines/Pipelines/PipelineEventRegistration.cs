using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public class PipelineEventRegistration(
    EntityStore<Pipeline> store,
    PipelineEvents events) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        return Result.Success();
    }
}
