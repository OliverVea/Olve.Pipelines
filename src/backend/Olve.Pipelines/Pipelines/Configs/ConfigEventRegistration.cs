using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Configs;

/// <summary>A deleted pipeline's config snapshots go with it.</summary>
public class ConfigEventRegistration(PipelineEvents pipelineEvents, IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<ConfigSnapshotService>().DeleteAll(e.Id));
        return Result.Success();
    }
}
