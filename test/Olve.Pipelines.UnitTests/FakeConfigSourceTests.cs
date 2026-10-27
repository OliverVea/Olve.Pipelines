using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class FakeConfigSourceTests
{
    private static PipelineConfigBinding Binding() => new(
        new IdProvider().Create<PipelineConfigBinding>(),
        new IdProvider().Create<Pipeline>(),
        "OliverVea/Olve.Pipelines",
        "main",
        ".pipelines",
        CredentialsSecret: null,
        LastPushSha: null,
        LastReconciledSha: null,
        ReconcileStatus.NeverRun,
        DateTimeOffset.UnixEpoch);

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    [Test]
    public async Task FetchConfig_WhenEtagMatches_ReturnsNotModified()
    {
        var source = new FakeConfigSource { ConfigETag = "etag-1" };

        var fetch = Pick(await source.FetchConfigAsync(Binding(), etag: "etag-1"));

        await Assert.That(fetch).IsTypeOf<ConfigFetch.NotModified>();
    }

    [Test]
    public async Task FetchConfig_WhenEtagDiffers_ReturnsChanged()
    {
        var source = new FakeConfigSource { ConfigETag = "etag-2", ConfigSha = "abc" };

        var fetch = Pick(await source.FetchConfigAsync(Binding(), etag: "stale"));

        var changed = fetch as ConfigFetch.Changed;
        await Assert.That(changed).IsNotNull();
        await Assert.That(changed!.Sha).IsEqualTo("abc");
    }

    [Test]
    public async Task GetBranchHead_ReturnsConfiguredSha()
    {
        var source = new FakeConfigSource { BranchHeadSha = "deadbeef" };
        await Assert.That(Pick(await source.GetBranchHeadShaAsync(Binding()))).IsEqualTo("deadbeef");
    }
}
