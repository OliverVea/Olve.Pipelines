using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Production;

public class ProductionStepEventRegistration(
    EntityStore<ProductionStep> store,
    ProductionStepEvents events,
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        pipelineEvents.OnDeleted.Subscribe(id => sp.GetRequiredService<ProductionStepCleanupService>().HandlePipelineDeleted(id));

        return Result.Success();
    }
}
