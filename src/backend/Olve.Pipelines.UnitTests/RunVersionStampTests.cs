using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class RunVersionStampTests
{
    [Test]
    public async Task Format_UsesUtcDate()
    {
        var local = new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.FromHours(2)); // 2026-10-21T23:00Z

        await Assert.That(RunVersionStamp.Format(local, 412)).IsEqualTo("2026.10.21.412");
    }

    [Test]
    [Arguments("2026.10.21.412", 412)]
    [Arguments("2026.01.02.1", 1)]
    public async Task TryParseRunNumber_ReadsLastSegment(string stamp, int expected)
    {
        await Assert.That(RunVersionStamp.TryParseRunNumber(stamp, out var n)).IsTrue();
        await Assert.That(n).IsEqualTo(expected);
    }

    [Test]
    [Arguments("412")]
    [Arguments("2026.10.21")]
    [Arguments("2026.10.21.-1")]
    [Arguments("2026.10.21.0")]
    [Arguments("2026.10.21.x")]
    public async Task TryParseRunNumber_RejectsMalformed(string stamp)
        => await Assert.That(RunVersionStamp.TryParseRunNumber(stamp, out _)).IsFalse();
}

public class StorageLayoutTests
{
    private static readonly Id<Pipeline> PipelineId = new(new Id(Guid.Parse("0123456789abcdef0123456789abcdef")));

    [Test]
    public async Task Keys_MirrorTheAddressScheme()
    {
        var attempt = TestAttempts.Make(PipelineId, "deploy-beta", runNumber: 412, number: 2);

        await Assert.That(StorageLayout.VersionBinaryKey(PipelineId, 8)).IsEqualTo("versions/0123456789abcdef0123456789abcdef/8/step.tar.gz");
        await Assert.That(StorageLayout.VersionManifestKey(PipelineId, 8)).IsEqualTo("versions/0123456789abcdef0123456789abcdef/8/manifest.json");
        await Assert.That(StorageLayout.RunStateKey(PipelineId, 412)).IsEqualTo("runs/0123456789abcdef0123456789abcdef/412/run.json");
        await Assert.That(StorageLayout.AttemptOutputKey(attempt)).IsEqualTo("runs/0123456789abcdef0123456789abcdef/412/deploy-beta/2/output.json");
        await Assert.That(StorageLayout.AttemptFilesPrefix(attempt)).IsEqualTo("runs/0123456789abcdef0123456789abcdef/412/deploy-beta/2/files");
        await Assert.That(StorageLayout.AttemptLogKey(attempt)).IsEqualTo("runs/0123456789abcdef0123456789abcdef/412/deploy-beta/2/log");
    }

    [Test]
    public async Task KubernetesJobName_IsDeterministicAndFitsK8sLimit()
    {
        var attempt = TestAttempts.Make();

        await Assert.That(attempt.KubernetesJobName).IsEqualTo($"olve-{attempt.Id.Value.Value:N}");
        await Assert.That(attempt.KubernetesJobName.Length).IsLessThanOrEqualTo(63);
    }
}
