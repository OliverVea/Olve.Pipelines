using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

/// <summary>Scheduling rules over attempts (the store → hub forwarding lives in <see cref="RunEventRegistration"/>).</summary>
public class JobEventRegistration(AttemptEvents attemptEvents, IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        attemptEvents.OnAdded.Subscribe(e => sp.GetRequiredService<JobObsoletionService>().HandleAttemptAdded(e.Id));
        attemptEvents.OnAdded.Subscribe(_ => sp.GetRequiredService<JobDispatchSignal>().Nudge());
        attemptEvents.OnUpdated.Subscribe(e =>
        {
            if (e.Before.Status is not AttemptStatus.Waiting && e.After.Status is AttemptStatus.Waiting)
                sp.GetRequiredService<JobObsoletionService>().HandleAttemptWaiting(e.After.Id);
        });
        attemptEvents.OnUpdated.Subscribe(e =>
        {
            // A preflight that ended (waiting, or scheduled to run) frees its step's key as well.
            if ((e.Before.Status.IsActive() && !e.After.Status.IsActive()) || (e.Before.Status is AttemptStatus.Waiting && e.After.Status is AttemptStatus.Scheduled)
                || (e.Before.Status is AttemptStatus.InProgress && e.After.Status is AttemptStatus.Waiting or AttemptStatus.Scheduled))
                sp.GetRequiredService<JobDispatchSignal>().Nudge();
        });

        return Result.Success();
    }
}
