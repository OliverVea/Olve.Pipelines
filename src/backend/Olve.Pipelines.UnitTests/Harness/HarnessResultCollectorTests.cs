using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results;
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

    // Catches: an oversized result being downloaded and unpacked into storage anyway.
    [Test]
    public async Task Collect_AnArchiveOverTheCap_FailsWithoutStoringAnything()
    {
        var (collector, s3) = Collector(new HarnessResultLimits { MaxArchiveBytes = 16 });
        s3.Objects[HarnessStorageKeys.AttemptResultArchiveKey(Attempt)] = Archive(
            ("output.json", """{"output":{},"status":"succeeded"}"""), ("files/big", new string('x', 1000)));

        var collection = await collector.CollectAsync(Attempt);

        var size = s3.Objects[HarnessStorageKeys.AttemptResultArchiveKey(Attempt)].Length;
        await collection.Archive!.Value.Problems.IsSingle<ResultProblem>($"Invalid result archive: result.tar.gz is {size} bytes; the limit is 16");
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

    // A restart before the attempt's status is saved collects the same archive again.
    [Test]
    public async Task Collect_Twice_GivesTheSameResult()
    {
        var (collector, s3) = Collector();
        s3.Objects[HarnessStorageKeys.AttemptResultArchiveKey(Attempt)] = Archive(
            ("output.json", """{"output":{"lines":3},"status":"succeeded"}"""), ("files/log.txt", "text"));

        var first = await collector.CollectAsync(Attempt);
        var stored = s3.Objects.ToDictionary(o => o.Key, o => o.Value);
        var second = await collector.CollectAsync(Attempt);

        await Assert.That(second.FileKeys).IsEquivalentTo(first.FileKeys);
        await Assert.That(second.Archive!.Value).Succeeded();
        await Assert.That(s3.Objects.Count).IsEqualTo(stored.Count);
        foreach (var (key, value) in stored)
            await Assert.That(s3.Objects[key]).IsEquivalentTo(value);
    }
}
