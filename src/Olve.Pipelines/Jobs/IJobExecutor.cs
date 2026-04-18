namespace Olve.Pipelines.Jobs;

public interface IJobExecutor
{
    void EnsureRunning(Id<Job> id);
}
