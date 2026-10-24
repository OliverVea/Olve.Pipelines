using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// The config cursor covers the bound directory plus the local path dependencies of its package.json
/// (an SDK-only commit must rebuild a TS pipeline that depends on <c>file:../sdk</c>).
/// </summary>
public class ConfigCursorTests
{
    [Test]
    [Arguments(".pipelines", """{"dependencies":{"@olivervea/pipelines":"file:../sdk","zod":"4.6.7"}}""", "sdk")]
    [Arguments(".pipelines/", """{"devDependencies":{"a":"link:../tools/a/"}}""", "tools/a")]
    [Arguments("deploy/.pipelines", """{"optionalDependencies":{"a":"file:../../lib"}}""", "lib")]
    [Arguments("", """{"dependencies":{"a":"file:./sdk"}}""", "sdk")]
    [Arguments(".pipelines", """{"dependencies":{"a":"file:.."}}""", "<root>")]
    [Arguments(".pipelines", """{"dependencies":{"a":"file:../sdk","b":"link:../sdk/"},"devDependencies":{"c":"file:../other"}}""", "sdk,other")]
    [Arguments(".pipelines", """{"dependencies":{"a":"file:../../outside","b":"file:/abs","c":"file:./lib","d":"link:.","e":"file:","f":"^1.0.0","g":"github:o/r","h":"file:C:\\x"}}""", "")]
    [Arguments(".pipelines", """{"peerDependencies":{"a":"file:../sdk"},"dependencies":["file:../sdk"]}""", "")]
    [Arguments(".pipelines", "not json", "")]
    public async Task Resolve_LocalPathDependencies(string bindingPath, string packageJson, string expected)
    {
        var resolved = LocalPathDependencies.Resolve(bindingPath, packageJson);

        // "" = none; "<root>" = the repo root (path "").
        List<string> expectedList = expected.Length == 0 ? [] : expected.Split(',').Select(e => e == "<root>" ? "" : e).ToList();
        await Assert.That(resolved).IsEquivalentTo(expectedList);
    }

    [Test]
    public async Task CursorState_RoundTrips_AndRejectsForeignETags()
    {
        var state = new ConfigCursorState([
            new PathCursor(".pipelines", "W/\"a\"", "aaa", DateTimeOffset.Parse("2026-10-21T10:00:00Z")),
            new PathCursor("sdk", "W/\"b\"", "bbb", DateTimeOffset.Parse("2026-10-21T11:00:00Z")),
        ]);

        var decoded = ConfigCursorState.Decode(state.Encode());

        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.Paths).IsEquivalentTo(state.Paths);
        await Assert.That(decoded.Sha).IsEqualTo("bbb");
        await Assert.That(ConfigCursorState.Decode(null)).IsNull();
        await Assert.That(ConfigCursorState.Decode("W/\"abc\"")).IsNull();
        await Assert.That(ConfigCursorState.Decode("olve-cursor:v1:%%%")).IsNull();
    }

    [Test]
    public async Task CursorState_NewestCommitWins_TiesGoToTheBoundDirectory()
    {
        var t = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var tie = new ConfigCursorState([new PathCursor(".pipelines", "", "main", t), new PathCursor("sdk", "", "dep", t)]);
        var older = new ConfigCursorState([new PathCursor(".pipelines", "", "main", t), new PathCursor("sdk", "", "dep", t.AddMinutes(-1))]);

        await Assert.That(tie.Sha).IsEqualTo("main");
        await Assert.That(older.Sha).IsEqualTo("main");
    }

    [Test]
    public async Task Fetch_SdkOnlyCommit_MovesTheCursor_WithoutRereadingPackageJson()
    {
        var gh = new FakeGitHub();
        gh.Commit(".pipelines", "p1", "2026-10-21T10:00:00Z");
        gh.Commit("sdk", "s1", "2026-10-21T09:00:00Z");
        gh.PackageJson[".pipelines/package.json@p1"] = """{"dependencies":{"@olivervea/pipelines":"file:../sdk"}}""";
        var source = new TestSource(gh);

        // First fetch: both paths, package.json read once; the directory's commit is newer.
        var first = (ConfigFetch.Changed)Pick(await source.FetchConfigAsync(Binding()));
        await Assert.That(first.Sha).IsEqualTo("p1");
        await Assert.That(gh.ContentReads).IsEqualTo(1);

        // Nothing changed: every path answers 304.
        var idle = Pick(await source.FetchConfigAsync(Binding(), first.ETag));
        await Assert.That(idle).IsTypeOf<ConfigFetch.NotModified>();

        // An SDK-only commit: the cursor moves to it; package.json is not re-read.
        gh.Commit("sdk", "s2", "2026-10-21T12:00:00Z");
        var sdk = (ConfigFetch.Changed)Pick(await source.FetchConfigAsync(Binding(), first.ETag));
        await Assert.That(sdk.Sha).IsEqualTo("s2");
        await Assert.That(gh.ContentReads).IsEqualTo(1);

        // A later directory commit wins again and re-reads package.json (which may drop the dependency).
        gh.Commit(".pipelines", "p2", "2026-10-21T13:00:00Z");
        gh.PackageJson[".pipelines/package.json@p2"] = """{"dependencies":{"zod":"4.6.7"}}""";
        var dir = (ConfigFetch.Changed)Pick(await source.FetchConfigAsync(Binding(), sdk.ETag));
        await Assert.That(dir.Sha).IsEqualTo("p2");
        await Assert.That(gh.ContentReads).IsEqualTo(2);
        await Assert.That(ConfigCursorState.Decode(dir.ETag)!.Paths.Select(p => p.Path)).IsEquivalentTo([".pipelines"]);
    }

    [Test]
    public async Task Fetch_WithoutPackageJson_WatchesOnlyTheBoundDirectory()
    {
        var gh = new FakeGitHub();
        gh.Commit(".pipelines", "p1", "2026-10-21T10:00:00Z");
        gh.Commit("sdk", "s1", "2026-10-21T11:00:00Z");
        var source = new TestSource(gh);

        var fetch = (ConfigFetch.Changed)Pick(await source.FetchConfigAsync(Binding()));

        await Assert.That(fetch.Sha).IsEqualTo("p1");
        await Assert.That(gh.CommitQueries.Distinct()).IsEquivalentTo([".pipelines"]);
    }

    [Test]
    public async Task Fetch_GitHubErrorOnADependency_IsAProblem()
    {
        var gh = new FakeGitHub();
        gh.Commit(".pipelines", "p1", "2026-10-21T10:00:00Z");
        gh.PackageJson[".pipelines/package.json@p1"] = """{"dependencies":{"a":"file:../sdk"}}""";
        gh.Failing.Add("sdk");

        var result = await new TestSource(gh).FetchConfigAsync(Binding());

        await Assert.That(result).Failed();
    }

    private static PipelineConfigBinding Binding() => new(
        Id.New<PipelineConfigBinding>(), Id.New<Pipeline>(), "acme/widgets", "main", ".pipelines", null, null, null,
        ReconcileStatus.NeverRun, DateTimeOffset.UnixEpoch);

    private static T Pick<T>(Result<T> result)
        => result.TryPickProblems(out var problems, out var value) ? throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString()))) : value;

    private sealed class TestSource(FakeGitHub gh) : GitHubConfigSource(null!, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<GitHubConfigSource>.Instance)
    {
        protected override HttpClient CreateClient() => new(gh, disposeHandler: false);
    }

    /// <summary>Just enough of GitHub's commits (conditional, per path) and raw contents APIs.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Sha, string Date, int Version)> _heads = new(StringComparer.Ordinal);

        public Dictionary<string, string> PackageJson { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
        public List<string> CommitQueries { get; } = [];
        public int ContentReads { get; private set; }

        public void Commit(string path, string sha, string date)
            => _heads[path] = (sha, date, _heads.TryGetValue(path, out var h) ? h.Version + 1 : 1);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (uri.AbsolutePath.EndsWith("/commits", StringComparison.Ordinal))
            {
                var path = query["path"] ?? "";
                CommitQueries.Add(path);
                if (Failing.Contains(path))
                    return Respond(HttpStatusCode.InternalServerError);
                if (!_heads.TryGetValue(path, out var head))
                    return Respond(HttpStatusCode.OK, "[]");

                var etag = $"W/\"{path}-{head.Version}\"";
                if (request.Headers.IfNoneMatch.Any(e => e.ToString() == etag))
                    return Respond(HttpStatusCode.NotModified);

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"sha\":\"" + head.Sha + "\",\"commit\":{\"committer\":{\"date\":\"" + head.Date + "\"}}}]", Encoding.UTF8, "application/json"),
                };
                response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
                return Task.FromResult(response);
            }

            if (uri.AbsolutePath.Contains("/contents/", StringComparison.Ordinal))
            {
                ContentReads++;
                var file = uri.AbsolutePath[(uri.AbsolutePath.IndexOf("/contents/", StringComparison.Ordinal) + "/contents/".Length)..];
                return PackageJson.TryGetValue($"{file}@{query["ref"]}", out var content)
                    ? Respond(HttpStatusCode.OK, content)
                    : Respond(HttpStatusCode.NotFound);
            }

            return Respond(HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string? body = null)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body ?? "") });
    }
}
