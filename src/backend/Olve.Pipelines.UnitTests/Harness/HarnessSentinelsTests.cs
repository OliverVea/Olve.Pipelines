using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.UnitTests.Manifests;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessSentinelsTests
{
    [Test]
    public async Task Result_LastBlockWins_UserOutputIgnored()
    {
        var log = """
            user says ---OLVE-RESULT-BEGIN--- inline (not a line start)
            ---OLVE-RESULT-BEGIN---
            {"status":"skipped","reason":"old"}
            ---OLVE-RESULT-END---
            some stderr
            ---OLVE-RESULT-BEGIN---
            {"output":{"a":1},"status":"succeeded"}
            ---OLVE-RESULT-END---

            """;

        await Assert.That(HarnessSentinels.TryExtractResult(log)).IsEqualTo("""{"output":{"a":1},"status":"succeeded"}""");
    }

    [Test]
    [Arguments("")]
    [Arguments("no sentinels here\n")]
    [Arguments("---OLVE-RESULT-BEGIN---\n{\"status\":\"skipped\"\n")]
    [Arguments("{\"status\":\"skipped\"}\n---OLVE-RESULT-END---\n")]
    public async Task Result_MissingOrIncomplete_IsNull(string log)
    {
        await Assert.That(HarnessSentinels.TryExtractResult(log)).IsNull();
    }

    [Test]
    public async Task Result_TrailingUnterminatedBlock_FallsBackToLastComplete()
    {
        var log = "---OLVE-RESULT-BEGIN---\n{\"a\":1}\n---OLVE-RESULT-END---\n---OLVE-RESULT-BEGIN---\n{\"trunc";

        await Assert.That(HarnessSentinels.TryExtractResult(log)).IsEqualTo("{\"a\":1}");
    }

    [Test]
    public async Task Manifest_RoundTripsByteExact()
    {
        var manifest = await File.ReadAllTextAsync(ManifestFixture.Path);
        var log = "olve-build: compiled\n" +
                  $"{HarnessSentinels.ManifestBegin}\n{manifest}{HarnessSentinels.ManifestEnd}\n";

        await Assert.That(HarnessSentinels.TryExtractManifest(log)).IsEqualTo(manifest);
        await Assert.That(manifest.EndsWith('\n')).IsTrue();
    }

    [Test]
    public async Task Manifest_InterleavedBuildLogLine_IsDropped()
    {
        var manifest = await File.ReadAllTextAsync(ManifestFixture.Path);
        var lines = manifest.Split('\n');
        var interleaved = string.Join('\n', lines[..5].Append("olve-build: uploaded step.tar.zst").Concat(lines[5..]));
        var log = $"{HarnessSentinels.ManifestBegin}\n{interleaved}{HarnessSentinels.ManifestEnd}\n";

        await Assert.That(HarnessSentinels.TryExtractManifest(log)).IsEqualTo(manifest);
    }
}
