using System.Text.Json.Nodes;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.UnitTests.Recordings;

public sealed class FreshnessTests : IDisposable
{
    private readonly List<string> _checkouts = [];

    public void Dispose()
    {
        foreach (var checkout in _checkouts)
            Directory.Delete(checkout, recursive: true);
    }

    private const string ReRecord = "re-record: dotnet run --project src/backend/Olve.Pipelines.Recorder -- <system>";

    private static readonly DateOnly Today = new(2026, 10, 31);

    private sealed class FixedClock(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private static Recording Recorded(string system, string version, int ageDays = 0) => new(
        system, $"r{Guid.NewGuid():N}", "test", Today.AddDays(-ageDays), version, [],
        new RecordedRequest("GET", "/", new Dictionary<string, string>()),
        new RecordedResponse(200, new Dictionary<string, string>(), null));

    private static string Summarize(IReadOnlyList<Recording> recordings, params SystemPin[] pins) =>
        Freshness.Summarize(new RecordingStore.Contents(recordings, []), pins, Today);

    private static readonly SystemPin DexPin = new("dex", "v2", "a");
    private static readonly SystemPin K3sPin = new("k3s", "v1", "b");

    [Test]
    public async Task CurrentRecordings_SayOnlyHowManyThereAre()
    {
        var line = Summarize([Recorded("github", "2022", ageDays: 60), Recorded("dex", "v2", ageDays: 900), Recorded("k3s", "v1")], DexPin, K3sPin);

        await Assert.That(line).IsEqualTo("recorded fixtures: 3 current");
    }

    [Test]
    public async Task GitHub_ExpiresByDate()
    {
        var line = Summarize([Recorded("github", "2022", ageDays: 61), Recorded("github", "2022", ageDays: 90), Recorded("github", "2022", ageDays: 1)]);

        await Assert.That(line).IsEqualTo($"recorded fixtures: 3 total; 2 expired (github, over 60 days); {ReRecord}");
    }

    [Test]
    public async Task AnUpgrade_MarksRecordingsStale_WhateverTheirAge()
    {
        var line = Summarize([Recorded("dex", "v1", ageDays: 0), Recorded("k3s", "v1", ageDays: 0)], DexPin, K3sPin);

        await Assert.That(line).IsEqualTo($"recorded fixtures: 2 total; 1 stale (dex: recorded v1, pinned v2); {ReRecord}");
    }

    [Test]
    public async Task PinsThatDisagree_MarkRecordingsStale()
    {
        var line = Summarize([Recorded("k3s", "v1")], K3sPin, new SystemPin("k3s", "v2", "c"));

        await Assert.That(line).IsEqualTo($"recorded fixtures: 1 total; 1 stale (k3s: recorded v1, pinned v1, v2); {ReRecord}");
    }

    [Test]
    public async Task AMissingPin_IsSaid_NotGuessed()
    {
        var line = Summarize([Recorded("dex", "v2")]);

        await Assert.That(line).IsEqualTo($"recorded fixtures: 1 total; no version pin found for dex; {ReRecord}");
    }

    [Test]
    public async Task EveryProblem_FitsOnOneLine()
    {
        var contents = new RecordingStore.Contents(
            [Recorded("github", "2022", ageDays: 100), Recorded("dex", "v1"), Recorded("k3s", "v0")],
            ["dex/broken.json"]);

        var line = Freshness.Summarize(contents, [DexPin, K3sPin], Today);

        await Assert.That(line).IsEqualTo(
            "recorded fixtures: 4 total; 1 expired (github, over 60 days); " +
            "2 stale (dex: recorded v1, pinned v2, k3s: recorded v0, pinned v1); " +
            $"1 unreadable (dex/broken.json); {ReRecord}");
    }

    private string NewCheckout(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"olve-recordings-{Guid.NewGuid():N}");
        _checkouts.Add(root);
        foreach (var (path, content) in files.Append(("Olve.Pipelines.slnx", "")))
        {
            var file = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        return root;
    }

    [Test]
    public async Task ExpiredAndBrokenRecordings_NeverFailTheRun()
    {
        var root = NewCheckout(
            ("tools/recordings/hand-written.json", "[]"),
            ("tools/recordings/github/old.json", Recorded("github", "2022", ageDays: 400).ToJson().Replace("\"name\": \"", "\"name\": \"old")),
            ("tools/recordings/github/no-date.json", ((Func<string>)(() =>
            {
                var node = JsonNode.Parse(Recorded("github", "2022").ToJson())!.AsObject();
                node.Remove("capturedAt");
                return node.ToJsonString();
            }))()),
            ("tools/recordings/k3s/not-json.json", "{"));
        var output = new StringWriter();

        RecordingSummaryHook.Run(root, new FixedClock(Today), output);

        const string Expected = "recorded fixtures: 3 total; 1 expired (github, over 60 days); " +
            "2 unreadable (github/no-date.json, k3s/not-json.json); " + ReRecord;
        await Assert.That(output.ToString()).IsEqualTo(Expected + Environment.NewLine);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, Freshness.SummaryFile))).IsEqualTo(Expected + "\n");
    }

    [Test]
    public async Task NoCheckout_IsSaid()
    {
        var output = new StringWriter();

        RecordingSummaryHook.Run(Path.GetPathRoot(Path.GetTempPath())!, new FixedClock(Today), output);

        await Assert.That(output.ToString()).IsEqualTo("recorded fixtures: not checked, no checkout found" + Environment.NewLine);
    }

    [Test]
    public async Task NoStore_IsSaid()
    {
        var root = NewCheckout();

        var line = Freshness.Report(root, new FixedClock(Today));

        await Assert.That(line).IsEqualTo($"recorded fixtures: not checked, DirectoryNotFoundException: Could not find a part of the path '{Path.Combine(root, "tools/recordings")}'.");
    }

    [Test]
    public async Task PinsThatDisagree_StopACapture()
    {
        var root = NewCheckout(("docker-compose.yml", "dexidp/dex:v1\n"), ("src/deploy/helm/values.yaml", "dexidp/dex:v2\n"));

        await Assert.That(() => SystemPins.Single(root, SystemPins.Dex)).Throws<InvalidOperationException>().WithMessage("expected one dex pin, found: v1, v2");
    }

    [Test]
    public async Task Pins_ComeFromTheRepo()
    {
        var root = NewCheckout(
            ("docker-compose.yml", "image: dexidp/dex:v2.1.0\nimage: rancher/k3s:v1.2.3-k3s1\n"),
            ("src/deploy/helm/values.yaml", "image: \"dexidp/dex:v2.2.0\"\n"),
            ("tools/real-system/compose.yml", "image: rancher/k3s:v1.2.3-k3s1\n"));

        var pins = SystemPins.Read(root);

        await Assert.That(pins).IsEquivalentTo(new[]
        {
            new SystemPin("dex", "v2.1.0", "docker-compose.yml"),
            new SystemPin("dex", "v2.2.0", "src/deploy/helm/values.yaml"),
            new SystemPin("k3s", "v1.2.3-k3s1", "docker-compose.yml"),
            new SystemPin("k3s", "v1.2.3-k3s1", "tools/real-system/compose.yml"),
        });
    }
}
