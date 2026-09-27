using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingEventRegistration(
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        pipelineEvents.OnDeleted.Subscribe(e =>
            sp.GetRequiredService<PipelineConfigBindingCleanupService>().HandlePipelineDeleted(e.Id));

        return Result.Success();
    }
}
