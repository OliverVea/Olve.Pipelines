using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>Unpacking a step's result with the real S3 client: what the service reads back must be what the step wrote.</summary>
public abstract class ResultCollectionContract : ContractSuite
{
    public sealed record Store(IAmazonS3 S3, string Bucket);

    protected abstract Task<ContractSubject<Store>> SubjectAsync();

    private static Attempt NewAttempt() => new(
        Id.New<Attempt>(), Id.New<Pipeline>(), Id.New<Run>(), 7, "build", 1, new AttemptStatus.InProgress(DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);

    private static HarnessResultCollector Collector(Store store) =>
        new(store.S3, new StorageOptions(store.Bucket), new HarnessOptions(), NullLogger<HarnessResultCollector>.Instance);

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

    private static async Task<string> ReadAsync(Store store, string key)
    {
        using var response = await store.S3.GetObjectAsync(store.Bucket, key);
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync();
    }

    [Test]
    public async Task Collect_StoresFilesAndOutput()
    {
        Rule();
        await using var s = await SubjectAsync();
        var attempt = NewAttempt();
        await s.Client.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = s.Client.Bucket,
            Key = HarnessStorageKeys.AttemptResultArchiveKey(attempt),
            InputStream = new MemoryStream(Archive(
                ("output.json", """{"output":{"message":"m"},"status":"succeeded"}"""),
                ("files/chart/values.yaml", "a: 1"),
                ("files/empty.txt", ""))),
        });

        var collection = await Collector(s.Client).CollectAsync(attempt);

        await Assert.That(collection.Archive!.Value).Succeeded();
        var files = StorageLayout.AttemptFilesPrefix(attempt);
        await Assert.That(collection.FileKeys).IsEquivalentTo([$"{files}/chart/values.yaml", $"{files}/empty.txt"]);
        await Assert.That(await ReadAsync(s.Client, $"{files}/chart/values.yaml")).IsEqualTo("a: 1");
        await Assert.That(await ReadAsync(s.Client, $"{files}/empty.txt")).IsEqualTo("");
        await Assert.That(await ReadAsync(s.Client, StorageLayout.AttemptOutputKey(attempt))).IsEqualTo("""{"message":"m"}""");
    }

    [Test]
    public async Task Collect_WithoutAnArchive_ReportsNone()
    {
        Rule();
        await using var s = await SubjectAsync();

        var collection = await Collector(s.Client).CollectAsync(NewAttempt());

        await Assert.That(collection.Archive).IsNull();
        await Assert.That(collection.FileKeys).IsEmpty();
    }

    [Test]
    public async Task GetObject_OfAMissingKey_ThrowsNoSuchKey()
    {
        Rule();
        await using var s = await SubjectAsync();

        var thrown = await Assert.That(() => s.Client.S3.GetObjectAsync(s.Client.Bucket, s.NewName())).Throws<AmazonS3Exception>();

        await Assert.That(thrown!.ErrorCode).IsEqualTo("NoSuchKey");
        await Assert.That(thrown.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetObject_ReportsTheLastModifiedTime()
    {
        Rule();
        await using var s = await SubjectAsync();
        var key = s.NewName();
        var before = DateTime.UtcNow.AddMinutes(-1);
        await s.Client.S3.PutObjectAsync(new PutObjectRequest { BucketName = s.Client.Bucket, Key = key, ContentBody = "x", UseChunkEncoding = false });

        using var response = await s.Client.S3.GetObjectAsync(s.Client.Bucket, key);

        await Assert.That(response.LastModified!.Value.ToUniversalTime()).IsGreaterThan(before);
    }
}
