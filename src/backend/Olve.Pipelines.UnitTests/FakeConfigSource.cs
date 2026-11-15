using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// In-memory <see cref="IConfigSource"/> for testing reconcile and the deploy poll without
/// network. Holds a branch-head SHA plus a config subtree (SHA + ETag + files) and serves
/// conditional fetches: a matching ETag yields <see cref="ConfigFetch.NotModified"/>.
/// </summary>
public sealed class FakeConfigSource : IConfigSource
{
    public string BranchHeadSha { get; set; } = "head-0";
    public string BranchHeadMessage { get; set; } = "Change something\n\nWhy it changed.";
    public string ConfigSha { get; set; } = "config-0";
    public string ConfigETag { get; set; } = "etag-0";

    /// <summary>The branch's history, oldest first: what <see cref="CompareAsync"/> compares within.</summary>
    public List<CommitInfo> History { get; } = [];

    public Result<CommitComparison>? CompareOverride { get; set; }
    public int Comparisons { get; private set; }

    public Result<BranchHead>? BranchHeadOverride { get; set; }
    public Result<ConfigFetch>? FetchOverride { get; set; }

    public Task<Result<BranchHead>> GetBranchHeadAsync(PipelineConfigBinding binding, CancellationToken ct = default)
        => Task.FromResult(BranchHeadOverride ?? new BranchHead(BranchHeadSha, BranchHeadMessage, DateTimeOffset.UnixEpoch));

    public Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default)
    {
        if (FetchOverride is { } overridden)
            return Task.FromResult(overridden);

        ConfigFetch fetch = etag == ConfigETag
            ? new ConfigFetch.NotModified(ConfigETag)
            : new ConfigFetch.Changed(ConfigSha, ConfigETag);

        return Task.FromResult<Result<ConfigFetch>>(fetch);
    }

    public Task<Result<CommitComparison>> CompareAsync(
        PipelineConfigBinding binding, string @base, string head, CancellationToken ct = default)
    {
        Comparisons++;
        if (CompareOverride is { } overridden)
            return Task.FromResult(overridden);

        var from = History.FindIndex(c => c.Sha == @base);
        var to = History.FindIndex(c => c.Sha == head);
        if (from < 0 || to < 0)
            return Task.FromResult<Result<CommitComparison>>(new ResultProblem("GitHub doesn't know commit {0} or {1} in '{2}'.", @base, head, binding.Repo));

        var between = History.Skip(from + 1).Take(Math.Max(0, to - from)).Reverse().ToList();
        return Task.FromResult<Result<CommitComparison>>(new CommitComparison(
            between.Take(CommitComparison.MaxListed).ToList(), between.Count, $"https://github.com/{binding.Repo}/compare/{@base}...{head}"));
    }
}
