namespace Olve.Pipelines.Configuration;

public interface ICredentialsProvider<T>
{
    Task<T> GetCredentialsAsync(CancellationToken ct = default);
}

public class DirectCredentialsProvider<T>(T credentials) : ICredentialsProvider<T>
{
    public Task<T> GetCredentialsAsync(CancellationToken ct = default) => Task.FromResult(credentials);
}
