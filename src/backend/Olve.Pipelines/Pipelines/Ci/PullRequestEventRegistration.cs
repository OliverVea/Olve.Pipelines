using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Ci;

public class PullRequestEventRegistration(
    EntityStore<PullRequest> store,
    PullRequestEvents events,
    PipelineEvents pipelineEvents,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        store.OnAdded.Subscribe(events.OnAdded.Invoke);
        store.OnUpdated.Subscribe(events.OnUpdated.Invoke);
        store.OnDeleted.Subscribe(events.OnDeleted.Invoke);

        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<PullRequestCleanupService>().HandlePipelineDeleted(e.Id));

        return Result.Success();
    }
}

public class PullRequestCleanupService(EntityStore<PullRequest> store, PullRequestService pulls)
{
    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        foreach (var pull in pulls.ListByPipeline(pipelineId))
            store.Delete(pull.Id).DiscardResult();
    }
}
