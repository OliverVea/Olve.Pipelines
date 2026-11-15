using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>CI's subscriptions (one line each): store → hubs, pull requests → runs, attempts → scheduling and dispatch.</summary>
public class CiEventRegistration(
    EntityStore<CiRun> runStore,
    EntityStore<CiAttempt> attemptStore,
    CiRunEvents runEvents,
    CiAttemptEvents attemptEvents,
    PullRequestEvents pullEvents,
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

        pullEvents.OnAdded.Subscribe(e => Trigger().Observe(null, e.Entity));
        pullEvents.OnUpdated.Subscribe(e => Trigger().Observe(e.Before, e.After));
        pullEvents.OnDeleted.Subscribe(e => Trigger().Forget(e.Entity));

        attemptEvents.OnAdded.Subscribe(_ => sp.GetRequiredService<JobDispatchSignal>().Nudge());
        attemptEvents.OnUpdated.Subscribe(e =>
        {
            if (e.Before.Status.IsActive() && e.After.Status.IsTerminal())
            {
                sp.GetRequiredService<JobDispatchSignal>().Nudge();
                sp.GetRequiredService<CiScheduler>().Advance(e.After.RunId);
            }
        });

        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<CiRunCleanupService>().HandlePipelineDeleted(e.Id));

        return Result.Success();
    }

    private CiTriggerService Trigger() => sp.GetRequiredService<CiTriggerService>();
}

/// <summary>Deletes a deleted pipeline's CI runs and attempts. Transient.</summary>
public class CiRunCleanupService(CiRunService runs, CiAttemptService attempts)
{
    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        foreach (var run in runs.ListByPipeline(pipelineId))
        {
            foreach (var attempt in attempts.ListByRun(run.Id))
                attempts.Delete(attempt.Id).DiscardResult();
            runs.Delete(run.Id).DiscardResult();
        }
    }
}

/// <summary>At startup, after persistence loaded: drops CI runs of pipelines that are gone and advances running ones.</summary>
public class CiStartup(EntityStore<Pipeline> pipelines, CiRunService runs, CiRunCleanupService cleanup, CiScheduler scheduler) : IRunOnStartup
{
    public Result Run()
    {
        foreach (var pipelineId in runs.List().Select(r => r.PipelineId).Distinct().Where(p => !pipelines.Contains(p)).ToList())
            cleanup.HandlePipelineDeleted(pipelineId);
        scheduler.AdvanceRunning();
        return Result.Success();
    }
}
