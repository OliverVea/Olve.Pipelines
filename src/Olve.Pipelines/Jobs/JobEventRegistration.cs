using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Jobs;

public class JobEventRegistration(
    EntityStore<Job> store,
    JobEvents events,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        events.OnAdded.Subscribe(id => sp.GetRequiredService<JobObsoletionService>().HandleJobAdded(id));

        return Result.Success();
    }
}
