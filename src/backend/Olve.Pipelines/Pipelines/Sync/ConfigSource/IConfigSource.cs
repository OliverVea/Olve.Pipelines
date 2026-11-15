namespace Olve.Pipelines.Pipelines.Sync.ConfigSource;

/// <summary>Reads branch state from a binding's git repository (GitHub; tests use an in-memory fake).</summary>
public interface IConfigSource
{
    /// <summary>The binding branch's head; moving past <see cref="PipelineConfigBinding.LastPushSha"/> fires the push trigger.</summary>
    Task<Result<BranchHead>> GetBranchHeadAsync(PipelineConfigBinding binding, CancellationToken ct = default);

    /// <summary>
    /// The newest commit touching the binding's path (or a local path dependency of its <c>package.json</c>).
    /// <paramref name="etag"/> is whatever a previous fetch returned; when nothing changed since, the result is
    /// <see cref="ConfigFetch.NotModified"/> (a conditional request with no rate-limit cost).
    /// </summary>
    Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default);

    /// <summary>The commits reachable from <paramref name="head"/> but not from <paramref name="base"/>.</summary>
    Task<Result<CommitComparison>> CompareAsync(
        PipelineConfigBinding binding, string @base, string head, CancellationToken ct = default);
}

/// <param name="Commits">Newest first; a long comparison lists only its first <see cref="MaxListed"/>.</param>
/// <param name="TotalCommits">How many commits lie between, listed or not.</param>
public sealed record CommitComparison(IReadOnlyList<CommitInfo> Commits, int TotalCommits, string Url)
{
    public const int MaxListed = 250;
}

/// <param name="Title">The first line of the message.</param>
/// <param name="Author">The author's GitHub login, else the git author name.</param>
public sealed record CommitInfo(string Sha, string Title, string Author, DateTimeOffset CommittedAt, string Url);

public abstract record ConfigFetch
{
    public sealed record NotModified(string ETag) : ConfigFetch;

    public sealed record Changed(string Sha, string ETag) : ConfigFetch;
}
