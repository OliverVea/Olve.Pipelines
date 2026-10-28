using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>What result collection found.</summary>
/// <param name="Archive">Null when no <c>result.tar.gz</c> exists; failed when it was malformed or over a cap.</param>
/// <param name="FileKeys">Garage keys of the stored <c>files/…</c> objects.</param>
public sealed record HarnessCollection(Result<HarnessResultContents>? Archive, IReadOnlyList<string> FileKeys);

/// <summary>
/// Unpacks an attempt's <c>result.tar.gz</c> (PUT by the harness to <see cref="HarnessStorageKeys.AttemptResultArchiveKey"/>)
/// into the attempt's storage layout: each <c>files/&lt;p&gt;</c> → <c>runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/files/&lt;p&gt;</c>,
/// and a succeeded envelope's output → <c>…/output.json</c>. Collection is idempotent; the archive is
/// kept until <see cref="DeleteArchiveAsync"/>.
/// Feed the result to <see cref="HarnessOutcome.Resolve"/>.
/// </summary>
public sealed class HarnessResultCollector(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    HarnessOptions options,
    ILogger<HarnessResultCollector> logger)
{
    public async Task<HarnessCollection> CollectAsync(Attempt attempt, CancellationToken ct = default)
    {
        var archiveKey = HarnessStorageKeys.AttemptResultArchiveKey(attempt);
        var filesPrefix = StorageLayout.AttemptFilesPrefix(attempt);
        var limits = options.ResultLimits;
        var fileKeys = new List<string>();

        var tempDir = Directory.CreateTempSubdirectory("olve-result-");
        try
        {
            var archivePath = Path.Combine(tempDir.FullName, "result.tar.gz");
            try
            {
                using var response = await s3.GetObjectAsync(storageOptions.Bucket, archiveKey, ct);
                if (response.ContentLength > limits.MaxArchiveBytes)
                    return new HarnessCollection(Fail("result.tar.gz is {0} bytes; the limit is {1}", response.ContentLength, limits.MaxArchiveBytes), fileKeys);
                await using var file = File.Create(archivePath);
                await response.ResponseStream.CopyToAsync(file, ct);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new HarnessCollection(null, fileKeys);
            }

            Result<HarnessResultContents> contents;
            await using (var archive = File.OpenRead(archivePath))
            {
                contents = await HarnessResultArchive.ReadAsync(archive, limits, async (objectPath, content, length, token) =>
                {
                    // Stage to disk: S3 PUTs want a known length / seekable body.
                    var staged = Path.Combine(tempDir.FullName, "entry");
                    await using (var f = File.Create(staged))
                        await content.CopyToAsync(f, token);
                    var key = $"{filesPrefix}/{objectPath}";
                    await s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = storageOptions.Bucket,
                        Key = key,
                        FilePath = staged,
                    }, token);
                    fileKeys.Add(key);
                }, ct);
            }

            if (contents.TryPickProblems(out var problems, out var parsed))
            {
                logger.LogProblems(LogLevel.Warning, problems,
                    "Result archive of step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}') is invalid",
                    attempt.StepName, attempt.RunNumber, attempt.Number, attempt.PipelineId);
                return new HarnessCollection(contents, fileKeys);
            }

            if (parsed.Envelope is HarnessEnvelope.Succeeded { OutputOrNull: { } output })
            {
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = storageOptions.Bucket,
                    Key = StorageLayout.AttemptOutputKey(attempt),
                    InputStream = new MemoryStream(Encoding.UTF8.GetBytes(output)),
                    ContentType = "application/json",
                }, ct);
            }

            logger.LogInformation("Collected result of step '{StepName}' (run #{RunNumber}, attempt {AttemptNumber}, pipeline '{PipelineId}'): {Status}, {FileCount} files",
                attempt.StepName, attempt.RunNumber, attempt.Number, attempt.PipelineId, parsed.Envelope.GetType().Name, fileKeys.Count);
            return new HarnessCollection(contents, fileKeys);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); }
            catch (IOException e) { logger.LogWarning(e, "Could not delete temp dir '{TempDir}'", tempDir.FullName); }
        }
    }

    /// <summary>
    /// Removes the attempt's <c>result.tar.gz</c>. Call only after the attempt's terminal status is
    /// persisted: until then a restarted controller must be able to collect again (idempotent — same keys).
    /// </summary>
    public async Task DeleteArchiveAsync(Attempt attempt, CancellationToken ct = default)
    {
        var key = HarnessStorageKeys.AttemptResultArchiveKey(attempt);
        try
        {
            await s3.DeleteObjectAsync(storageOptions.Bucket, key, ct);
        }
        catch (AmazonS3Exception e)
        {
            logger.LogWarning(e, "Could not delete result archive '{Key}'", key);
        }
    }

    private static Result<HarnessResultContents> Fail(string format, params object[] args)
        => Result.Failure<HarnessResultContents>(new ResultProblem("Invalid result archive: " + format, args));
}
