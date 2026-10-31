using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.UnitTests.Recordings;

public class ScrubberTests
{
    private static readonly string Root = RecordingStore.FindRepoRoot(AppContext.BaseDirectory)!;

    // Assembled, so the repo itself holds nothing a scanner would flag.
    public static IEnumerable<string> Secrets()
    {
        yield return "ghp_" + new string('a', 36);
        yield return "gho_" + new string('b', 36);
        yield return "ghs_" + new string('c', 36);
        yield return "github_pat_" + new string('d', 40);
        yield return "Bearer " + new string('e', 30);
        yield return "bearer " + new string('e', 30);
        yield return "Basic " + Convert.ToBase64String(new byte[12]);
        yield return "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhIn0." + new string('f', 40);
        yield return "-----BEGIN RSA PRIVATE KEY-----\nabc\ndef\n-----END RSA PRIVATE KEY-----";
        yield return "-----BEGIN PGP SIGNATURE-----\nabc\n-----END PGP SIGNATURE-----";
        yield return "X-Amz-Signature=" + new string('0', 64);
        yield return "X-Amz-Credential=" + "GK" + new string('1', 24) + "%2F20261031";
        yield return "GK" + new string('2', 24);
        yield return "https://user:hunter2@github.com/o/r.git";
        yield return "someone@example.com";
    }

    [Test]
    [MethodDataSource(nameof(Secrets))]
    public async Task Scrub_CatchesEachSecret_AndLeaksSeesIt(string secret)
    {
        var scrubbed = Scrubber.Scrub($"before {secret} after");

        await Assert.That(scrubbed).DoesNotContain(secret);
        await Assert.That(scrubbed).StartsWith("before ");
        await Assert.That(Scrubber.Leaks(scrubbed)).IsEmpty();
        await Assert.That(Scrubber.Leaks(secret)).IsNotEmpty();
    }

    [Test]
    public async Task Scrub_CoversValuesAtAnyDepth_AndFieldsBySecretName()
    {
        var body = JsonNode.Parse($$"""
            {
              "items": [{ "note": "token {{"ghp_" + new string('a', 36)}}" }],
              "client_secret": "plain-words",
              "Password": "plain-words",
              "keys": [{ "kty": "RSA", "n": "public", "e": "AQAB", "d": "private", "p": "private" }],
              "token_endpoint": "http://localhost/token",
              "count": 3
            }
            """);

        var scrubbed = Scrubber.Scrub(body);

        var expected = JsonNode.Parse("""
            {
              "items": [{ "note": "token <scrubbed>" }],
              "client_secret": "<scrubbed>",
              "Password": "<scrubbed>",
              "keys": [{ "kty": "RSA", "n": "public", "e": "AQAB", "d": "<scrubbed>", "p": "<scrubbed>" }],
              "token_endpoint": "http://localhost/token",
              "count": 3
            }
            """);
        await Assert.That(JsonNode.DeepEquals(scrubbed, expected)).IsTrue();
    }

    [Test]
    public async Task KeepHeaders_DropsWhatIsNotAllowed()
    {
        var kept = Scrubber.KeepHeaders(
            [KeyValuePair.Create("Authorization", "Bearer x"), KeyValuePair.Create("Set-Cookie", "a=b"), KeyValuePair.Create("ETag", "W/\"1\"")],
            "etag");

        await Assert.That(kept.Keys).IsEquivalentTo(new[] { "etag" });
        await Assert.That(kept["etag"]).IsEqualTo("W/\"1\"");
    }

    [Test]
    public async Task Elide_RemovesPaths_ThroughLists()
    {
        var body = JsonNode.Parse("""[{"sha":"a","commit":{"author":"x","date":1}},{"sha":"b","commit":{"author":"y"}}]""");

        var elided = Scrubber.Elide(body, ["commit.author", "missing.path"]);

        await Assert.That(elided!.ToJsonString()).IsEqualTo("""[{"sha":"a","commit":{"date":1}},{"sha":"b","commit":{}}]""");
    }

    [Test]
    public async Task CommittedRecordings_HoldNoSecrets()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, RecordingStore.Directory), "*.json", SearchOption.AllDirectories).ToList();

        var leaks = files.SelectMany(f => Scrubber.Leaks(File.ReadAllText(f)).Select(leak => $"{f}: {leak}")).ToList();

        await Assert.That(files).IsNotEmpty();
        await Assert.That(leaks).IsEmpty();
    }

    [Test]
    public async Task CommittedRecordings_CarryTheirCaptureMetadata()
    {
        var contents = RecordingStore.Load(Root);

        await Assert.That(contents.Unreadable).IsEmpty();
        await Assert.That(contents.Recordings.Select(r => r.System).Distinct()).IsEquivalentTo(new[] { "dex", "github", "k3s" });
    }

    [Test]
    public async Task HandWrittenFixtures_AreListed_WithoutCaptureMetadata()
    {
        var path = Path.Combine(Root, RecordingStore.Directory, "hand-written.json");
        using var listing = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        foreach (var entry in listing.RootElement.EnumerateArray())
        {
            var fields = entry.EnumerateObject().Select(p => p.Name);
            await Assert.That(fields).IsEquivalentTo(new[] { "path", "standsFor", "notRecordedBecause" });
            await Assert.That(File.Exists(Path.Combine(Root, entry.GetProperty("path").GetString()!))).IsTrue();
        }
    }
}
