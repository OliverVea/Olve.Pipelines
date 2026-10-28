namespace Olve.Pipelines.Jobs;

public abstract record NoOpJobResult
{
    public record Success(string? OutputJson = null) : NoOpJobResult;
    public record Failure(string Reason) : NoOpJobResult;
    public record Skip(string Reason) : NoOpJobResult;
}
