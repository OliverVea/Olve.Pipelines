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
    public string ConfigSha { get; set; } = "config-0";
    public string ConfigETag { get; set; } = "etag-0";
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Result<string>? BranchHeadOverride { get; set; }

    public Task<Result<string>> GetBranchHeadShaAsync(PipelineConfigBinding binding, CancellationToken ct = default)
        => Task.FromResult(BranchHeadOverride ?? BranchHeadSha);

    public Task<Result<ConfigFetch>> FetchConfigAsync(
        PipelineConfigBinding binding, string? etag = null, CancellationToken ct = default)
    {
        ConfigFetch fetch = etag == ConfigETag
            ? new ConfigFetch.NotModified(ConfigETag)
            : new ConfigFetch.Changed(ConfigSha, ConfigETag, new Dictionary<string, string>(Files, StringComparer.Ordinal));

        return Task.FromResult<Result<ConfigFetch>>(fetch);
    }
}
