using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Runs;

public class RunEventRegistration(
    EntityStore<Run> runStore,
    EntityStore<Attempt> attemptStore,
    RunEvents runEvents,
    AttemptEvents attemptEvents,
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        runStore.OnAdded.Subscribe(runEvents.OnAdded.Invoke);
        runStore.OnUpdated.Subscribe(runEvents.OnUpdated.Invoke);
        runStore.OnDeleted.Subscribe(runEvents.OnDeleted.Invoke);

        attemptStore.OnAdded.Subscribe(attemptEvents.OnAdded.Invoke);
        attemptStore.OnUpdated.Subscribe(attemptEvents.OnUpdated.Invoke);
        attemptStore.OnDeleted.Subscribe(attemptEvents.OnDeleted.Invoke);

        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<RunCleanupService>().HandlePipelineDeleted(e.Id));
        runEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<RunCleanupService>().HandleRunDeleted(e.Entity));

        return Result.Success();
    }
}
