using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

public class JobLogService(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    AttemptService attempts)
{
    public async Task<Result<string>> GetLogsAsync(Id<Attempt> attemptId, CancellationToken ct)
    {
        if (!attempts.TryGet(attemptId, out var attempt))
            return new ResultProblem("Attempt '{0}' not found.", attemptId);

        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = StorageLayout.AttemptLogKey(attempt),
            }, ct);

            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ResultProblem("Logs not available for attempt '{0}'.", attemptId);
        }
    }
}
