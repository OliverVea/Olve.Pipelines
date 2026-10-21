using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Jobs;

/// <summary>Scheduling rules over attempts (the store → hub forwarding lives in <see cref="RunEventRegistration"/>).</summary>
public class JobEventRegistration(AttemptEvents attemptEvents, IServiceProvider sp) : IRunOnStartup
{
    public Result Run()
    {
        attemptEvents.OnAdded.Subscribe(e => sp.GetRequiredService<JobObsoletionService>().HandleAttemptAdded(e.Id));

        return Result.Success();
    }
}
