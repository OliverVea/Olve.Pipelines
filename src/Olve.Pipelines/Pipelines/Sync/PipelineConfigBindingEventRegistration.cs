using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Sync;

public class PipelineConfigBindingEventRegistration(
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        pipelineEvents.OnDeleted.Subscribe(id =>
            sp.GetRequiredService<PipelineConfigBindingCleanupService>().HandlePipelineDeleted(id));

        return Result.Success();
    }
}
