using System.Text.Json;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Shared.Storage;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessFileRefsTests
{
    private static readonly string Sha = new('a', 64);

    private static S3UrlPresigner Presigner() => new(
        new PresignOptions(new Uri("http://garage.svc:3900"), "bucket", "us-east-1", TimeSpan.FromMinutes(10)),
        new DirectCredentialsProvider<S3Credentials>(new S3Credentials("GKid", "secret")),
        TimeProvider.System);

    [Test]
    public async Task Find_LocatesRefsAnywhere_AndDeduplicates()
    {
        using var doc = JsonDocument.Parse($$"""
            {
              "build": { "image": { "run": 412, "step": "build", "attempt": 1, "path": "image.tar", "sha256": "{{Sha}}" } },
              "source": { "tree": { "run": 410, "step": "source", "attempt": 2, "path": "tree/", "sha256": "{{Sha}}" } },
              "list": [ { "run": 412, "step": "build", "attempt": 1, "path": "image.tar", "sha256": "{{Sha}}" } ],
              "notARef": { "run": 1, "step": "x", "attempt": 1, "path": "p", "sha256": "{{Sha}}", "extra": true },
              "wrongTypes": { "run": "1", "step": "x", "attempt": 1, "path": "p", "sha256": "{{Sha}}" }
            }
            """);

        var refs = HarnessFileRefs.Find(doc.RootElement);

        await Assert.That(refs.Select(r => r.UrlKey).ToArray()).IsEquivalentTo(new[] { "410/source/2/tree/", "412/build/1/image.tar" });
    }

    [Test]
    public async Task ObjectPath_DirectoriesBecomeTars()
    {
        await Assert.That(new HarnessFileRef(1, "build", 1, "chart/", Sha).ObjectPath).IsEqualTo("chart.tar");
        await Assert.That(new HarnessFileRef(1, "build", 1, "a/b/", Sha).ObjectPath).IsEqualTo("a/b.tar");
        await Assert.That(new HarnessFileRef(1, "build", 1, "a/b.txt", Sha).ObjectPath).IsEqualTo("a/b.txt");
    }

    [Test]
    public async Task UrlMap_KeysByRefAddress_PointsAtStoredObject()
    {
        var refs = new[]
        {
            new HarnessFileRef(410, "source", 2, "tree/", Sha),
            new HarnessFileRef(412, "build", 1, "image.tar", Sha),
            new HarnessFileRef(412, "build", 1, "image.tar", Sha),
        };

        var map = await HarnessFileRefs.BuildUrlMapAsync(HarnessJobBuilderTests.PipelineId, refs, Presigner(), TimeSpan.FromMinutes(30));

        await Assert.That(map).Succeeded();
        var urls = map.Value!;
        await Assert.That(urls.Count).IsEqualTo(2);
        await Assert.That(urls["410/source/2/tree/"]).StartsWith("http://garage.svc:3900/bucket/runs/11111111222233334444555555555555/410/source/2/files/tree.tar?");
        await Assert.That(urls["412/build/1/image.tar"]).StartsWith("http://garage.svc:3900/bucket/runs/11111111222233334444555555555555/412/build/1/files/image.tar?");
        await Assert.That(urls["412/build/1/image.tar"]).Contains("X-Amz-Expires=1800&");
    }

    [Test]
    [Arguments(1, "build", 1, "../escape")]
    [Arguments(1, "build", 1, "/abs")]
    [Arguments(1, "build", 1, "a/../b")]
    [Arguments(1, "build", 1, "a\\b")]
    [Arguments(1, "build", 1, "./a")]
    [Arguments(1, "build", 1, "")]
    [Arguments(1, "Build", 1, "a")]
    [Arguments(1, "../x", 1, "a")]
    [Arguments(-1, "build", 1, "a")]
    [Arguments(1, "build", 0, "a")]
    public async Task UrlMap_RejectsInvalidRefs(int run, string step, int attempt, string path)
    {
        var map = await HarnessFileRefs.BuildUrlMapAsync(HarnessJobBuilderTests.PipelineId,
            [new HarnessFileRef(run, step, attempt, path, Sha)], Presigner(), TimeSpan.FromMinutes(30));

        await Assert.That(map).Failed();
    }

    [Test]
    public async Task UrlMap_RejectsBadSha()
    {
        var map = await HarnessFileRefs.BuildUrlMapAsync(HarnessJobBuilderTests.PipelineId,
            [new HarnessFileRef(1, "build", 1, "a", "XYZ")], Presigner(), TimeSpan.FromMinutes(30));

        await Assert.That(map).Failed();
    }

    [Test]
    public async Task ToJson_IsCompactAndSorted()
    {
        var json = HarnessFileRefs.ToJson(new Dictionary<string, string> { ["b"] = "http://x/?a=1&b=2", ["a"] = "u" });

        await Assert.That(json).IsEqualTo("""{"a":"u","b":"http://x/?a=1&b=2"}""");
    }
}
