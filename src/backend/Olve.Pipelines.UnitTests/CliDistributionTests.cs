using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Olve.Pipelines.Distribution;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

public class CliDistributionTests
{
    private static CliReleaseQuery Query(TestRunDomain d, string? pipeline, string step = "publish-cli")
        => new(new DistributionOptions(pipeline, step), d.PipelineService, d.Query);

    [Test]
    public async Task LatestRelease_IsTheConfiguredPipelinesLatestSucceededPublish()
    {
        var d = new TestRunDomain();
        var pipeline = d.CreatePipeline("Olve.Pipelines v2");
        var version = d.AddVersion(pipeline.Id);
        var good = d.Schedule("publish-cli", d.CreateRun(version));
        await Assert.That(d.AttemptService.MarkSucceeded(good.Id)).Succeeded();
        var newerFailed = d.Schedule("publish-cli", d.CreateRun(version));
        await Assert.That(d.AttemptService.MarkFailed(newerFailed.Id, "link failed")).Succeeded();

        await Assert.That(Query(d, "Olve.Pipelines v2").TryGetLatest(out var byName)).IsTrue();
        await Assert.That(byName!.Id).IsEqualTo(good.Id);
        await Assert.That(Query(d, pipeline.Id.ToString()).TryGetLatest(out var byId)).IsTrue();
        await Assert.That(byId!.Id).IsEqualTo(good.Id);
    }

    [Test]
    public async Task LatestRelease_NoneWhenUnconfigured_AmbiguousOrOnlyAnotherPipelinePublished()
    {
        var d = new TestRunDomain();
        var other = d.CreatePipeline("someone else's repo");
        var attempt = d.Schedule("publish-cli", d.CreateRun(d.AddVersion(other.Id)));
        await Assert.That(d.AttemptService.MarkSucceeded(attempt.Id)).Succeeded();
        d.CreatePipeline("Olve.Pipelines v2");

        // Not configured: nothing is served, even though some pipeline has a successful publish-cli.
        await Assert.That(Query(d, null).TryGetLatest(out _)).IsFalse();
        // Configured, but that pipeline never published.
        await Assert.That(Query(d, "Olve.Pipelines v2").TryGetLatest(out _)).IsFalse();

        // A name two pipelines share resolves to neither.
        d.CreatePipeline("someone else's repo");
        await Assert.That(Query(d, "someone else's repo").TryGetLatest(out _)).IsFalse();
    }

    [Test]
    public async Task LatestRelease_WithPromotedBy_OnlyCountsRunsWhereThatStepSucceeded()
    {
        var d = new TestRunDomain();
        var pipeline = d.CreatePipeline("Olve.Pipelines v2");
        var version = d.AddVersion(pipeline.Id);
        var promotedRun = d.CreateRun(version);
        var promoted = d.Schedule("publish-cli", promotedRun);
        await Assert.That(d.AttemptService.MarkSucceeded(promoted.Id)).Succeeded();
        await Assert.That(d.AttemptService.MarkSucceeded(d.Schedule("promote", promotedRun).Id)).Succeeded();
        var prerelease = d.Schedule("publish-cli", d.CreateRun(version));
        await Assert.That(d.AttemptService.MarkSucceeded(prerelease.Id)).Succeeded();
        var query = new CliReleaseQuery(new DistributionOptions("Olve.Pipelines v2", "publish-cli", "promote"), d.PipelineService, d.Query);

        await Assert.That(query.TryGetLatest(out var served)).IsTrue();
        await Assert.That(served!.Id).IsEqualTo(promoted.Id);
        await Assert.That(Query(d, "Olve.Pipelines v2").TryGetLatest(out var unpromoted)).IsTrue();
        await Assert.That(unpromoted!.Id).IsEqualTo(prerelease.Id);
        var options = DistributionOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Distribution:CliPromotedBy"] = "promote" }).Build());
        await Assert.That(options.CliPromotedBy).IsEqualTo("promote");
    }

    [Test]
    public async Task Options_DefaultStep_AndUnsetPipeline()
    {
        var empty = DistributionOptions.From(new ConfigurationBuilder().Build());
        await Assert.That(empty.CliPipeline).IsNull();
        await Assert.That(empty.CliStep).IsEqualTo("publish-cli");

        var set = DistributionOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Distribution:CliPipeline"] = " Olve.Pipelines v2 " })
            .Build());
        await Assert.That(set.CliPipeline).IsEqualTo("Olve.Pipelines v2");
    }

    [Test]
    public async Task InstallScripts_DefaultToTheServingInstance_OverrideStillWins()
    {
        foreach (var name in new[] { "install.sh", "install.ps1" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, name));
            // The rewrite anchors on the one placeholder; it must exist exactly once (the INSTANCE assignment).
            await Assert.That(script.Split(CliDistribution.ScriptDefaultBaseUrl).Length - 1).IsEqualTo(1);

            var rewritten = CliDistribution.RewriteDefaultBaseUrl(script, "https://pipelines-beta.ovea.pro");
            await Assert.That(rewritten).DoesNotContain(CliDistribution.ScriptDefaultBaseUrl);
            await Assert.That(rewritten).Contains("https://pipelines-beta.ovea.pro");
            await Assert.That(rewritten).Contains("PL_API_URL");
        }
    }

    [Test]
    public async Task InstanceBaseUrl_PrefersLinksBaseUrl_ElseTheRequestOrigin()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("olve-pipelines-v2.apps-beta");

        var none = new ConfigurationBuilder().Build();
        var links = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Links:BaseUrl"] = "https://pipelines-beta.ovea.pro/" })
            .Build();

        await Assert.That(CliDistribution.InstanceBaseUrl(http.Request, none)).IsEqualTo("http://olve-pipelines-v2.apps-beta");
        await Assert.That(CliDistribution.InstanceBaseUrl(http.Request, links)).IsEqualTo("https://pipelines-beta.ovea.pro");
    }

    [Test]
    [Arguments("pl-linux-x64", true)]
    [Arguments("pl-win-x64.exe", true)]
    [Arguments("output.json", false)]
    [Arguments("../pl-linux-x64", false)]
    public async Task OnlyAllowListedAssetsResolve(string asset, bool allowed)
        => await Assert.That(CliDistribution.Assets.Contains(asset)).IsEqualTo(allowed);

    [Test]
    public async Task LocalDirectory_ServesBinariesBuiltNextToTheInstance()
    {
        var dir = Directory.CreateTempSubdirectory("pl-dist-").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "pl-linux-x64"), "binary");
        var local = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Distribution:LocalDirectory"] = dir })
            .Build();

        await Assert.That(CliDistribution.LocalAsset(local, "pl-linux-x64")).IsEqualTo(Path.Combine(dir, "pl-linux-x64"));
        await Assert.That(CliDistribution.LocalAsset(local, "pl-win-x64.exe")).IsNull();
        await Assert.That(CliDistribution.LocalAsset(new ConfigurationBuilder().Build(), "pl-linux-x64")).IsNull();
        await Assert.That(CliDistribution.LocalBuildPending(local, "pl-linux-x64")).IsFalse();
        await Assert.That(CliDistribution.LocalBuildPending(local, "pl-win-x64.exe")).IsTrue();
        await Assert.That(CliDistribution.LocalBuildPending(new ConfigurationBuilder().Build(), "pl-win-x64.exe")).IsFalse();
    }
}
