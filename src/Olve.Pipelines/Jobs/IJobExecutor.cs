using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

public interface IJobExecutor
{
    void EnsureRunning(Id<Attempt> id);
}
