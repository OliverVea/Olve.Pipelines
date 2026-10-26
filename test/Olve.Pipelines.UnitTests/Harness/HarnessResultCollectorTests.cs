using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessResultCollectorTests
{
    private static readonly Attempt Attempt = new(
        Id.New<Attempt>(), Id.New<Pipeline>(), Id.New<Run>(), 7, "build", 1, new AttemptStatus.InProgress(DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);

    private static byte[] Archive(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
        return buffer.ToArray();
    }

    private static (HarnessResultCollector Collector, FakeS3 S3) Collector(HarnessResultLimits? limits = null)
    {
        var s3 = new FakeS3();
        var collector = new HarnessResultCollector(s3.Client(), new StorageOptions(FakeS3.Bucket),
            new HarnessOptions { ResultLimits = limits ?? new HarnessResultLimits() }, NullLogger<HarnessResultCollector>.Instance);
        return (collector, s3);
    }

    // Catches: a step's files or output not landing at the keys later steps and the API read from.
    [Test]
    public async Task Collect_StoresFilesAndOutput_UnderTheAttemptsLayout()
    {
        var (collector, s3) = Collector();
        s3.Objects[HarnessStorageKeys.AttemptResultArchiveKey(Attempt)] = Archive(
            ("output.json", """{"output":{"message":"m"},"status":"succeeded"}"""),
            ("files/chart/values.yaml", "a: 1"),
            ("files/image.tar", "tar"));

        var collection = await collector.CollectAsync(Attempt);

        await Assert.That(collection.Archive!.Value).Succeeded();
        var files = StorageLayout.AttemptFilesPrefix(Attempt);
        await Assert.That(collection.FileKeys).IsEquivalentTo([$"{files}/chart/values.yaml", $"{files}/image.tar"]);
        await Assert.That(Encoding.UTF8.GetString(s3.Objects[$"{files}/chart/values.yaml"])).IsEqualTo("a: 1");
        await Assert.That(Encoding.UTF8.GetString(s3.Objects[StorageLayout.AttemptOutputKey(Attempt)])).IsEqualTo("""{"message":"m"}""");
    }

    [Test]
    public async Task Collect_WithoutAnArchive_ReportsNone()
    {
        var (collector, s3) = Collector();

        var collection = await collector.CollectAsync(Attempt);

        await Assert.That(collection.Archive).IsNull();
        await Assert.That(s3.Puts).IsEmpty();
    }

    // Catches: an oversized result being downloaded and unpacked into storage anyway.
    [Test]
    public async Task Collect_AnArchiveOverTheCap_FailsWithoutStoringAnything()
    {
        var (collector, s3) = Collector(new HarnessResultLimits { MaxArchiveBytes = 16 });
        s3.Objects[HarnessStorageKeys.AttemptResultArchiveKey(Attempt)] = Archive(
            ("output.json", """{"output":{},"status":"succeeded"}"""), ("files/big", new string('x', 1000)));

        var collection = await collector.CollectAsync(Attempt);

        await Assert.That(collection.Archive!.Value).Failed();
        await Assert.That(s3.Puts).IsEmpty();
    }

    [Test]
    public async Task Collect_AFailedEnvelope_StoresFilesButNoOutput()
    {
        var (collector, s3) = Collector();
        s3.Objects[HarnessStorageKeys.AttemptResultArchiveKey(Attempt)] = Archive(
            ("output.json", """{"error":{"kind":"handler","message":"boom"},"status":"failed"}"""), ("files/log.txt", "partial"));

        var collection = await collector.CollectAsync(Attempt);

        await Assert.That(collection.Archive!.Value).Succeeded();
        await Assert.That(collection.FileKeys).Count().IsEqualTo(1);
        await Assert.That(s3.Objects.ContainsKey(StorageLayout.AttemptOutputKey(Attempt))).IsFalse();
    }

    [Test]
    public async Task DeleteArchive_RemovesOnlyTheArchive()
    {
        var (collector, s3) = Collector();
        var archiveKey = HarnessStorageKeys.AttemptResultArchiveKey(Attempt);
        s3.Objects[archiveKey] = Archive(("output.json", """{"output":{},"status":"succeeded"}"""));
        await collector.CollectAsync(Attempt);

        await collector.DeleteArchiveAsync(Attempt);

        await Assert.That(s3.Objects.ContainsKey(archiveKey)).IsFalse();
        await Assert.That(s3.Objects.ContainsKey(StorageLayout.AttemptOutputKey(Attempt))).IsTrue();
    }
}
