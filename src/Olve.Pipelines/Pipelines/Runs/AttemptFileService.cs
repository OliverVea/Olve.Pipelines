using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>An opened stored file: stream it to the client, then dispose.</summary>
public sealed class AttemptFileDownload(GetObjectResponse response, string fileName) : IDisposable
{
    public Stream Content => response.ResponseStream;
    public string ContentType { get; } = string.IsNullOrEmpty(response.Headers.ContentType) || response.Headers.ContentType == "binary/octet-stream"
        ? "application/octet-stream"
        : response.Headers.ContentType;
    public long? Length => response.ContentLength > 0 ? response.ContentLength : null;
    public string FileName { get; } = fileName;

    public void Dispose() => response.Dispose();
}

/// <summary>
/// Output files of attempts in Garage (spec §5.5): <c>runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/files/&lt;object path&gt;</c>.
/// A file ref's <c>path</c> maps to its object path by the storage rule (sdk/README.md): files as-is,
/// directories (<c>dir/</c>) as <c>dir.tar</c>. Transient.
/// </summary>
public class AttemptFileService(IAmazonS3 s3, StorageOptions storageOptions)
{
    /// <summary>Maps a file-ref path to its stored object path, rejecting unsafe paths.</summary>
    public static Result<string> ToObjectPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return new ResultProblem("A file path is required.");
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains('\0'))
            return new ResultProblem("File path '{0}' must be relative, without '\\' or NUL.", path);

        var isDirectory = path.EndsWith('/');
        var trimmed = isDirectory ? path[..^1] : path;
        var segments = trimmed.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
            return new ResultProblem("File path '{0}' has an empty, '.' or '..' segment.", path);

        return isDirectory ? trimmed + ".tar" : trimmed;
    }

    public async Task<AttemptFilesResponse> ListAsync(Attempt attempt, CancellationToken ct)
    {
        var prefix = StorageLayout.AttemptFilesPrefix(attempt) + "/";
        var files = new List<AttemptFileEntry>();
        string? token = null;
        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = storageOptions.Bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);

            foreach (var o in response.S3Objects ?? [])
                files.Add(new AttemptFileEntry(o.Key[prefix.Length..], o.Size ?? 0, o.LastModified is { } m ? new DateTimeOffset(m.ToUniversalTime()) : null));

            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);

        return new AttemptFilesResponse(attempt.RunNumber, attempt.StepName, attempt.Number,
            files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Opens a stored object for streaming; null when it does not exist.</summary>
    public async Task<AttemptFileDownload?> OpenAsync(Attempt attempt, string objectPath, CancellationToken ct)
    {
        try
        {
            var response = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = StorageLayout.AttemptFileKey(attempt.PipelineId, attempt.RunNumber, attempt.StepName, attempt.Number, objectPath),
            }, ct);
            return new AttemptFileDownload(response, objectPath.Split('/')[^1]);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
