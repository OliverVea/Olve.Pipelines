using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>Event entry points of the scheduler; each takes the scheduling lock. Transient.</summary>
public class RunSchedulingHandler(
    StageScheduler scheduler,
    SwitchService switches,
    RunSchedulingLock schedulingLock)
{
    public void HandleRunAdded(Id<Run> runId) => scheduler.Advance(runId);

    /// <summary>An attempt reached a terminal status: advance its run, then check a pending drain (in
    /// that order, under the lock, so a drain never completes between a stage settling and the next
    /// stage being scheduled).</summary>
    public void HandleAttemptFinished(Attempt attempt) => schedulingLock.Run(() =>
    {
        scheduler.Advance(attempt.RunId);
        switches.TryCompleteDrain(attempt.PipelineId);
    });

    /// <summary>A gate was blocked/unblocked: unblocking resumes runs held at it.</summary>
    public void HandleGatesChanged(Id<Pipeline> pipelineId) => scheduler.AdvanceRunning(pipelineId);
}

/// <summary>Scheduler subscriptions (one line each): run creation, attempt completion, gate changes.</summary>
public class RunSchedulingEventRegistration(
    RunEvents runEvents,
    AttemptEvents attemptEvents,
    AttachmentStore<Pipeline, PipelineStepGates> gates,
    IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        runEvents.OnAdded.Subscribe(e => { if (e.Entity.Status is RunStatus.Running) Handler().HandleRunAdded(e.Id); });
        attemptEvents.OnUpdated.Subscribe(e => { if (e.Before.Status.IsActive() && e.After.Status.IsTerminal()) Handler().HandleAttemptFinished(e.After); });
        gates.OnSet.Subscribe(id => Handler().HandleGatesChanged(id));
        gates.OnRemoved.Subscribe(id => Handler().HandleGatesChanged(id));

        return Result.Success();
    }

    private RunSchedulingHandler Handler() => sp.GetRequiredService<RunSchedulingHandler>();
}

/// <summary>
/// Startup sweep (after persistence load and event wiring): drops runs whose pipeline no longer exists
/// and attempts whose run is gone, completes drains that finished while down, starts queued requests on
/// active pipelines, and advances every Running run (idempotent — resumes scheduling after a restart).
/// </summary>
public class RunSchedulingStartup(
    EntityStore<Pipeline> pipelines,
    EntityStore<Run> runStore,
    RunService runs,
    AttemptService attempts,
    StageScheduler scheduler,
    SwitchService switches,
    RunSchedulingLock schedulingLock,
    ILogger<RunSchedulingStartup> logger) : IRunOnStartup
{
    public Result Run()
    {
        schedulingLock.Run(() =>
        {
            foreach (var run in runStore.List().Where(r => !pipelines.Contains(r.PipelineId)))
            {
                logger.LogInformation("Dropping run #{RunNumber} ('{RunId}'): its pipeline '{PipelineId}' no longer exists",
                    run.Number, run.Id, run.PipelineId);
                _ = runs.Delete(run.Id);
            }

            foreach (var attempt in attempts.List().Where(a => !runStore.Contains(a.RunId)))
            {
                logger.LogInformation("Dropping attempt {AttemptNumber} of step '{StepName}' ('{AttemptId}'): its run no longer exists",
                    attempt.Number, attempt.StepName, attempt.Id);
                _ = attempts.Delete(attempt.Id);
            }

            foreach (var run in runStore.List().Where(r => r.Status is RunStatus.Running).OrderBy(r => r.Number))
                scheduler.Advance(run.Id);

            foreach (var pipeline in pipelines.List())
            {
                if (switches.TryCompleteDrain(pipeline.Id))
                    switches.OnSwitchCompleted(pipeline.Id);
            }
        });

        return Result.Success();
    }
}
