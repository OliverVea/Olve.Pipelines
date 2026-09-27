using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Processing;

public class ProcessingStepEventRegistration(
    EntityStore<ProcessingStep> store,
    ProcessingStepEvents events,
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<ProcessingStepCleanupService>().HandlePipelineDeleted(e.Id));

        return Result.Success();
    }
}
