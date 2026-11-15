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
    EntityStore<CiQueueEntry> queueStore,
    CiQueueEvents queueEvents,
    AttachmentStore<Pipeline, Versions.PipelineActivation> activations,
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
        queueStore.OnAdded.Subscribe(queueEvents.OnAdded.Invoke);
        queueStore.OnUpdated.Subscribe(queueEvents.OnUpdated.Invoke);
        queueStore.OnDeleted.Subscribe(queueEvents.OnDeleted.Invoke);

        pullEvents.OnAdded.Subscribe(e => Trigger().Observe(null, e.Entity));
        pullEvents.OnUpdated.Subscribe(e => Trigger().Observe(e.Before, e.After));
        pullEvents.OnDeleted.Subscribe(e => Trigger().Forget(e.Entity));
        pullEvents.OnAdded.Subscribe(e => Queue().Observe(null, e.Entity));
        pullEvents.OnUpdated.Subscribe(e => Queue().Observe(e.Before, e.After));
        pullEvents.OnDeleted.Subscribe(e => Queue().Forget(e.Entity));

        runEvents.OnAdded.Subscribe(e => Statuses().Report(e.Entity));
        runEvents.OnUpdated.Subscribe(e =>
        {
            if (e.Before.Status != e.After.Status || e.Before.Building != e.After.Building)
                Statuses().Report(e.After);
            if (e.After.IsQueue && e.Before.Status != e.After.Status)
                sp.GetRequiredService<CiQueueProcessor>().Nudge();
        });

        attemptEvents.OnAdded.Subscribe(e =>
        {
            sp.GetRequiredService<JobDispatchSignal>().Nudge();
            if (e.Entity.Status.IsTerminal())
                Statuses().Report(e.Entity.RunId);
        });
        attemptEvents.OnUpdated.Subscribe(e =>
        {
            if (e.Before.Status.IsActive() && e.After.Status.IsTerminal())
            {
                sp.GetRequiredService<JobDispatchSignal>().Nudge();
                sp.GetRequiredService<CiScheduler>().Advance(e.After.RunId);
                Statuses().Report(e.After.RunId);
            }
        });

        pipelineEvents.OnDeleted.Subscribe(e => sp.GetRequiredService<CiRunCleanupService>().HandlePipelineDeleted(e.Id));
        activations.OnSet.Subscribe(id => Trigger().ChecksChanged(id));

        return Result.Success();
    }

    private CiTriggerService Trigger() => sp.GetRequiredService<CiTriggerService>();

    private CiQueueActions Queue() => sp.GetRequiredService<CiQueueActions>();

    private CiStatusReporter Statuses() => sp.GetRequiredService<CiStatusReporter>();
}

/// <summary>Deletes a deleted pipeline's CI runs, attempts and merge queue. Transient.</summary>
public class CiRunCleanupService(CiRunService runs, CiAttemptService attempts, CiBuildService builds, CiQueueService queue)
{
    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        queue.DeletePipeline(pipelineId);
        foreach (var build in builds.List().Where(b => b.PipelineId == pipelineId))
            builds.Forget(pipelineId, build.CommitSha);
        foreach (var run in runs.ListByPipeline(pipelineId))
        {
            foreach (var attempt in attempts.ListByRun(run.Id))
                attempts.Delete(attempt.Id).DiscardResult();
            runs.Delete(run.Id).DiscardResult();
        }
    }
}

/// <summary>
/// At startup, after persistence loaded: drops CI runs of pipelines that are gone, advances running ones, resumes the
/// builds running runs wait for, and starts the runs open pull requests were still waiting for.
/// </summary>
public class CiStartup(
    EntityStore<Pipeline> pipelines,
    EntityStore<PullRequest> pulls,
    CiRunService runs,
    CiRunCleanupService cleanup,
    CiScheduler scheduler,
    CiDefinitionService definitions,
    CiStatusReporter statuses,
    CiTriggerService trigger) : IRunOnStartup
{
    public Result Run()
    {
        foreach (var pipelineId in runs.List().Select(r => r.PipelineId).Distinct().Where(p => !pipelines.Contains(p)).ToList())
            cleanup.HandlePipelineDeleted(pipelineId);
        scheduler.AdvanceRunning();
        definitions.ResumeBuilding();
        statuses.ReportLatest(pulls.List().Where(p => p.State == PullRequestState.Open));
        trigger.Resume(pulls.List());
        return Result.Success();
    }
}
