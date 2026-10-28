using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

/// <summary>
/// An attempt's log. Finished attempts read the persisted log from Garage; a running attempt returns its
/// pod's current output; attempts that never ran (scheduled, skipped by the scheduler, obsolete,
/// cancelled before starting) return a one-line explanation instead of an error.
/// </summary>
public class JobLogService(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    AttemptService attempts,
    IAttemptLiveLogs liveLogs)
{
    public async Task<Result<string>> GetLogsAsync(Id<Attempt> attemptId, CancellationToken ct)
    {
        if (!attempts.TryGet(attemptId, out var attempt))
            return new NotFoundProblem("Attempt '{0}' not found.", attemptId);

        switch (attempt.Status)
        {
            case Scheduled:
                return Note(attempt, "is scheduled; no logs yet");
            case InProgress:
                return await liveLogs.TryGetAsync(attempt, ct) ?? Note(attempt, "is starting; no logs yet");
        }

        if (await TryReadPersistedAsync(attempt, ct) is { } persisted)
            return persisted;

        return attempt.Status switch
        {
            Skipped s when attempt.IsSchedulerSkip() => Note(attempt, $"was skipped without running: {s.Reason}"),
            Obsolete => Note(attempt, "was made obsolete by a newer attempt before it ran"),
            Cancelled { StartedAt: null } => Note(attempt, "was cancelled before it started"),
            Failed { Reason: { } reason } => Note(attempt, $"failed without a log: {reason}"),
            _ => new ResultProblem("Logs not available for attempt '{0}'.", attemptId),
        };
    }

    private async Task<string?> TryReadPersistedAsync(Attempt attempt, CancellationToken ct)
    {
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
            return null;
        }
    }

    private static string Note(Attempt attempt, string what)
        => $"(attempt {attempt.Number} of step '{attempt.StepName}' in run #{attempt.RunNumber} {what})\n";
}
