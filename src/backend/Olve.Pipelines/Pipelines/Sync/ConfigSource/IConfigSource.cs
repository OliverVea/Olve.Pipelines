namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

/// <summary>Reads branch state from a binding's git repository (GitHub; tests use an in-memory fake).</summary>
public interface IConfigSource
{
    /// <summary>The binding branch's head; moving past <see cref="PipelineConfigBinding.LastPushSha"/> fires the push trigger.</summary>
    Task<Result<string>> GetBranchHeadShaAsync(PipelineConfigBinding binding, CancellationToken ct = default);

    /// <summary>
    /// The newest commit touching the binding's path (or a local path dependency of its <c>package.json</c>).
    /// <paramref name="etag"/> is whatever a previous fetch returned; when nothing changed since, the result is
    /// <see cref="ConfigFetch.NotModified"/> (a conditional request with no rate-limit cost).
    /// </summary>
    Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default);
}

public abstract record ConfigFetch
{
    public sealed record NotModified(string ETag) : ConfigFetch;

    public sealed record Changed(string Sha, string ETag) : ConfigFetch;
}
