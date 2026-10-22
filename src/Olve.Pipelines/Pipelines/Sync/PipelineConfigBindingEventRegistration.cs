using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingEventRegistration(
    EntityStore<PipelineConfigBinding> store,
    PipelineConfigBindingEvents events,
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        pipelineEvents.OnDeleted.Subscribe(e =>
            sp.GetRequiredService<PipelineConfigBindingCleanupService>().HandlePipelineDeleted(e.Id));

        return Result.Success();
    }
}
