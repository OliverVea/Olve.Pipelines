using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Jobs;
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
        LastDeployedSha: null,
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
        source.Files["config.yaml"] = "name: x";

        var fetch = Pick(await source.FetchConfigAsync(Binding(), etag: "etag-1"));

        await Assert.That(fetch).IsTypeOf<ConfigFetch.NotModified>();
    }

    [Test]
    public async Task FetchConfig_WhenEtagDiffers_ReturnsChangedWithFiles()
    {
        var source = new FakeConfigSource { ConfigETag = "etag-2", ConfigSha = "abc" };
        source.Files["config.yaml"] = "name: x";

        var fetch = Pick(await source.FetchConfigAsync(Binding(), etag: "stale"));

        var changed = fetch as ConfigFetch.Changed;
        await Assert.That(changed).IsNotNull();
        await Assert.That(changed!.Sha).IsEqualTo("abc");
        await Assert.That(changed.Files["config.yaml"]).IsEqualTo("name: x");
    }

    [Test]
    public async Task GetBranchHead_ReturnsConfiguredSha()
    {
        var source = new FakeConfigSource { BranchHeadSha = "deadbeef" };
        await Assert.That(Pick(await source.GetBranchHeadShaAsync(Binding()))).IsEqualTo("deadbeef");
    }
}
