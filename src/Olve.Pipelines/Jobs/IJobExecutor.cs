namespace Olve.Pipelines.Jobs;

public interface IJobExecutor
{
    Task<JobExecutionResult> ExecuteAsync(Job job, CancellationToken ct);
}

public abstract record JobExecutionResult
{
    public record Success : JobExecutionResult;
    public record Failure(string Reason) : JobExecutionResult;
}
