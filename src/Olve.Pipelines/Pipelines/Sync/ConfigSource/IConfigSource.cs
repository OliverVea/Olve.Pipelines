namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

/// <summary>
/// Reads pipeline configuration and branch state from a binding's git repository. The seam
/// keeps reconcile and the deploy poll testable without network — the GitHub implementation
/// ships in v1; tests use an in-memory fake.
/// </summary>
public interface IConfigSource
{
    /// <summary>
    /// The latest commit SHA on the binding's branch — the source of the deploy cursor.
    /// Advancing past <see cref="PipelineConfigBinding.LastDeployedSha"/> fires a production build.
    /// </summary>
    Task<Result<string>> GetBranchHeadShaAsync(PipelineConfigBinding binding, CancellationToken ct = default);

    /// <summary>
    /// Fetches the config files under the binding's path together with the head commit SHA
    /// touching that path. A conditional request keyed on <paramref name="etag"/> returns
    /// <see cref="ConfigFetch.NotModified"/> (no body, no rate-limit cost) when the config
    /// subtree is unchanged. Consumed by the Phase 4 reconcile loop.
    /// </summary>
    Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default);
}

/// <summary>Outcome of a conditional config fetch.</summary>
public abstract record ConfigFetch
{
    /// <summary>The config subtree is unchanged since <paramref name="ETag"/>.</summary>
    public sealed record NotModified(string ETag) : ConfigFetch;

    /// <summary>
    /// The config subtree changed. <paramref name="Files"/> are keyed by path relative to the
    /// binding's config directory (e.g. <c>config.yaml</c>, <c>steps/build.yaml</c>).
    /// </summary>
    public sealed record Changed(string Sha, string ETag, IReadOnlyDictionary<string, string> Files) : ConfigFetch;
}
