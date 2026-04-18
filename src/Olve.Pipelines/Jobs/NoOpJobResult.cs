namespace Olve.Pipelines.Jobs;

public abstract record NoOpJobResult
{
    public record Success : NoOpJobResult;
    public record Failure(string Reason) : NoOpJobResult;
}
