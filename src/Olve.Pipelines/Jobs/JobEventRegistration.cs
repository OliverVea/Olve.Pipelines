using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

public class JobEventRegistration(
    EntityStore<Job> store,
    JobEvents events,
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        events.OnAdded.Subscribe(id => sp.GetRequiredService<JobObsoletionService>().HandleJobAdded(id));
        pipelineEvents.OnDeleted.Subscribe(id => sp.GetRequiredService<JobCancellationService>().HandlePipelineDeleted(id));

        return Result.Success();
    }
}
