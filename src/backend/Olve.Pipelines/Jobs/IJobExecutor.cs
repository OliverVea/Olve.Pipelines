using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

public interface IJobExecutor
{
    void EnsureRunning(Id<Attempt> id);

    /// <summary>Starts (or reattaches) the watcher of a CI attempt.</summary>
    void EnsureRunning(Id<Pipelines.Ci.CiAttempt> id);
}
