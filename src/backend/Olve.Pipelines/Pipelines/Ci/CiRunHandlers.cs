using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using System.Text.Json;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.Streaming;
using Olve.TypeSpec;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>Starts a new run of the pull request's head; its running run is superseded. Replies once the run is saved.</summary>
[OperationHandler]
public sealed class RunPullHandler(PipelineService pipelines, CiRunStartService starts, CiRunViewService views, CiPersistenceService durability)
    : Contract.ICiPullsRunHandler
{
    public async Task<Contract.CiPullsRunResponse> HandleAsync(Contract.CiPullsRunRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return new Contract.CiPullsRunResponse.NotFound(PipelineViews.NotFound(id));

        var started = await starts.StartAsync(id, request.Pull, ct: cancellationToken);
        if (started.TryPickProblems(out var problems, out var start))
            return Fail(problems);
        if (start.Run is not { } run)
            return new Contract.CiPullsRunResponse.Conflict(ApiProblems.Of(new ConflictProblem(start.Skipped switch
            {
                CiStartSkip.NoCi => "The pipeline's active version declares no CI.",
                CiStartSkip.Conflict => "Pull request #{0} doesn't merge onto the base branch.",
                _ => "Pull request #{0} moved on while the run was starting.",
            }, request.Pull)));
        if ((await durability.SavedAsync(cancellationToken)).TryPickProblems(out var unsaved))
            return Fail(unsaved);
        return views.Detail(run);
    }

    private static Contract.CiPullsRunResponse Fail(ResultProblemCollection problems) => RunViews.Fail<Contract.CiPullsRunResponse>(problems,
        p => new Contract.CiPullsRunResponse.NotFound(p), p => new Contract.CiPullsRunResponse.Conflict(p),
        p => new Contract.CiPullsRunResponse.BadRequest(p), p => new Contract.CiPullsRunResponse.ServiceUnavailable(p));
}

[OperationHandler]
public sealed class GetCiRunHandler(CiRunService runs, CiRunViewService views) : Contract.ICiRunsGetHandler
{
    public Task<Contract.CiRunsGetResponse> HandleAsync(Contract.CiRunsGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.CiRunsGetResponse>(runs.TryGetByNumber(id, request.Number, out var run)
            ? views.Detail(run)
            : new Contract.CiRunsGetResponse.NotFound(ApiProblems.Of(CiRunViewsExtensions.NoRun(id, request.Number))));
    }
}

/// <summary>Cancels the run and its attempts in flight (their Jobs are deleted). Replies once that is saved.</summary>
[OperationHandler]
public sealed class CancelCiRunHandler(CiRunService runs, CiCancellationService cancellation, CiRunViewService views, CiPersistenceService durability)
    : Contract.ICiRunsCancelHandler
{
    public async Task<Contract.CiRunsCancelResponse> HandleAsync(Contract.CiRunsCancelRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!runs.TryGetByNumber(id, request.Number, out var run))
            return new Contract.CiRunsCancelResponse.NotFound(ApiProblems.Of(CiRunViewsExtensions.NoRun(id, request.Number)));
        if (cancellation.End(run, RunStatus.Cancelled).TryPickProblems(out var problems, out var ended)
            || (await durability.SavedAsync(cancellationToken)).TryPickProblems(out problems))
            return RunViews.Fail<Contract.CiRunsCancelResponse>(problems,
                p => new Contract.CiRunsCancelResponse.NotFound(p), p => new Contract.CiRunsCancelResponse.Conflict(p),
                p => new Contract.CiRunsCancelResponse.Conflict(p), p => new Contract.CiRunsCancelResponse.ServiceUnavailable(p));
        return views.Detail(ended);
    }
}

[OperationHandler]
public sealed class ListCiRunAttemptsHandler(CiRunService runs, CiAttemptService attempts) : Contract.ICiRunsAttemptsHandler
{
    public Task<Contract.CiRunsAttemptsResponse> HandleAsync(Contract.CiRunsAttemptsRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.CiRunsAttemptsResponse>(runs.TryGetByNumber(id, request.Number, out var run)
            ? new Contract.CiRunsAttemptsResponse.Ok([.. attempts.ListByRun(run.Id).Select(CiRunViewService.ToApi)])
            : new Contract.CiRunsAttemptsResponse.NotFound(ApiProblems.Of(CiRunViewsExtensions.NoRun(id, request.Number))));
    }
}

/// <summary>The stored files of a CI attempt (without <c>attempt</c> the latest successful one).</summary>
[OperationHandler]
public sealed class ListCiStepFilesHandler(CiAttemptResolver resolver, CiAttemptService attempts, AttemptFileService files) : Contract.ICiRunStepsFilesHandler
{
    public async Task<Contract.CiRunStepsFilesResponse> HandleAsync(Contract.CiRunStepsFilesRequest request, CancellationToken cancellationToken)
    {
        if (!resolver.TryResolve(request.PipelineId, request.Number, request.Step, request.Attempt, latestSucceeded: true, out var attempt))
            return new Contract.CiRunStepsFilesResponse.NotFound(ApiProblems.Of(CiAttemptResolver.NoAttempt(request.Number, request.Step, request.Attempt)));
        if (attempts.Origin(attempt) is not { } origin)
            return new Contract.CiRunStepsFilesResponse.NotFound(ApiProblems.Of(CiAttemptResolver.OriginGone(attempt)));
        var identity = HarnessAttemptIdentity.Of(origin);
        var listed = await files.ListAsync(identity.FilesPrefix, origin.RunNumber, origin.StepName, origin.Number, cancellationToken);
        return new Contract.CiRunStepsFilesResponse.Ok(RunViews.ToApi(listed with { Run = attempt.RunNumber, Step = attempt.StepName, Attempt = attempt.Number }));
    }
}

[OperationHandler]
public sealed class DownloadCiStepFileHandler(CiAttemptResolver resolver, CiAttemptService attempts, AttemptFileService files, IHttpContextAccessor http) : Contract.ICiRunStepsDownloadHandler
{
    public async Task<Contract.CiRunStepsDownloadResponse> HandleAsync(Contract.CiRunStepsDownloadRequest request, CancellationToken cancellationToken)
    {
        if (AttemptFileService.ToObjectPath(request.Path).TryPickProblems(out var pathProblems, out var objectPath))
            return RunViews.Fail<Contract.CiRunStepsDownloadResponse>(pathProblems,
                p => new Contract.CiRunStepsDownloadResponse.NotFound(p), null, p => new Contract.CiRunStepsDownloadResponse.BadRequest(p));
        if (!resolver.TryResolve(request.PipelineId, request.Number, request.Step, request.Attempt, latestSucceeded: true, out var attempt))
            return new Contract.CiRunStepsDownloadResponse.NotFound(ApiProblems.Of(CiAttemptResolver.NoAttempt(request.Number, request.Step, request.Attempt)));

        if (attempts.Origin(attempt) is not { } origin)
            return new Contract.CiRunStepsDownloadResponse.NotFound(ApiProblems.Of(CiAttemptResolver.OriginGone(attempt)));
        var download = await files.OpenAsync($"{HarnessAttemptIdentity.Of(origin).FilesPrefix}/{objectPath}", objectPath, cancellationToken);
        if (download is null)
            return new Contract.CiRunStepsDownloadResponse.NotFound(ApiProblems.Of(new NotFoundProblem(
                "Attempt {0} of step '{1}' in CI run #{2} has no file '{3}'.", attempt.Number, request.Step, request.Number, objectPath)));
        http.HttpContext?.Response.RegisterForDispose(download);
        return new Contract.CiRunStepsDownloadResponse.Ok(download.Content, download.ContentType, DownloadStepFileHandler.Attachment(download.FileName), download.Length);
    }
}

[OperationHandler]
public sealed class GetCiStepOutputHandler(CiAttemptResolver resolver) : Contract.ICiRunStepsOutputHandler
{
    public Task<Contract.CiRunStepsOutputResponse> HandleAsync(Contract.CiRunStepsOutputRequest request, CancellationToken cancellationToken)
    {
        if (!resolver.TryResolve(request.PipelineId, request.Number, request.Step, request.Attempt, latestSucceeded: true, out var attempt))
            return Task.FromResult<Contract.CiRunStepsOutputResponse>(new Contract.CiRunStepsOutputResponse.NotFound(
                ApiProblems.Of(CiAttemptResolver.NoAttempt(request.Number, request.Step, request.Attempt))));
        return Task.FromResult<Contract.CiRunStepsOutputResponse>(new Contract.StepOutput
        {
            Run = request.Number,
            Step = request.Step,
            Attempt = attempt.Number,
            Status = attempt.Status.State(),
            Output = attempt.Output is { } o ? JsonDocument.Parse(o.Json).RootElement.Clone() : null,
            OutputKey = attempt.Output?.Key,
        });
    }
}

/// <summary>Without <c>attempt</c> the latest attempt (whatever its status).</summary>
[OperationHandler]
public sealed class GetCiStepLogsHandler(CiAttemptResolver resolver, CiLogService logs) : Contract.ICiRunStepsLogsHandler
{
    public async Task<Contract.CiRunStepsLogsResponse> HandleAsync(Contract.CiRunStepsLogsRequest request, CancellationToken cancellationToken)
    {
        if (!resolver.TryResolve(request.PipelineId, request.Number, request.Step, request.Attempt, latestSucceeded: false, out var attempt))
            return new Contract.CiRunStepsLogsResponse.NotFound(ApiProblems.Of(new NotFoundProblem("CI run #{0} has no such attempt of step '{1}'.", request.Number, request.Step)));
        var complete = attempt.Status.IsTerminal();
        return new Contract.StepLog
        {
            Run = request.Number,
            Step = request.Step,
            Attempt = attempt.Number,
            Status = attempt.Status.State(),
            Complete = complete,
            Text = await logs.GetAsync(attempt, cancellationToken),
        };
    }
}

/// <summary>Without <c>attempt</c> the latest attempt. Ends with <c>end</c> once the attempt is terminal.</summary>
[OperationHandler]
public sealed class StreamCiStepLogsHandler(CiAttemptResolver resolver, CiLogService logs) : Contract.ICiRunStepsLogStreamHandler
{
    public Task<Contract.CiRunStepsLogStreamResponse> HandleAsync(Contract.CiRunStepsLogStreamRequest request, CancellationToken cancellationToken)
    {
        if (!resolver.TryResolve(request.PipelineId, request.Number, request.Step, request.Attempt, latestSucceeded: false, out var attempt))
            return Task.FromResult<Contract.CiRunStepsLogStreamResponse>(new Contract.CiRunStepsLogStreamResponse.NotFound(
                ApiProblems.Of(new NotFoundProblem("CI run #{0} has no such attempt of step '{1}'.", request.Number, request.Step))));
        return Task.FromResult<Contract.CiRunStepsLogStreamResponse>(new Contract.CiRunStepsLogStreamResponse.Ok(logs.StreamAsync(attempt.Id, cancellationToken)));
    }
}

/// <summary>Finds a CI attempt by its address <c>#run/step[@attempt]</c>. Transient.</summary>
public class CiAttemptResolver(CiRunService runs, CiAttemptService attempts)
{
    public bool TryResolve(Guid pipelineId, int number, string step, int? attemptNumber, bool latestSucceeded,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CiAttempt? attempt)
    {
        attempt = null;
        if (!runs.TryGetByNumber(PipelineViews.PipelineId(pipelineId), number, out var run))
            return false;
        var list = attempts.ListForStep(run.Id, step);
        attempt = attemptNumber is { } n ? list.FirstOrDefault(a => a.Number == n)
            : latestSucceeded ? list.LastOrDefault(a => a.Status is Succeeded)
            : list.LastOrDefault();
        return attempt is not null;
    }

    public static NotFoundProblem OriginGone(CiAttempt attempt) => new(
        "Attempt {0} of step '{1}' in CI run #{2} reuses CI run #{3}/{4}@{5}, which no longer exists.",
        attempt.Number, attempt.StepName, attempt.RunNumber, attempt.ReusedFrom!.Run, attempt.ReusedFrom.Step, attempt.ReusedFrom.Attempt);

    public static NotFoundProblem NoAttempt(int number, string step, int? attempt) => new(attempt is null
        ? "CI run #{0} has no successful attempt of step '{1}'."
        : "CI run #{0} has no attempt {2} of step '{1}'.", number, step, attempt ?? 0);
}

/// <summary>
/// A CI attempt's log: the stored one once it finished, its pod's while it runs, or a line saying why there is none.
/// The stream sends the live log as it grows, then the stored log and <c>end</c>. Transient.
/// </summary>
public class CiLogService(CiAttemptService attempts, IAmazonS3 s3, StorageOptions storageOptions, IAttemptLiveLogs liveLogs, TimeProvider time)
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<string> GetAsync(CiAttempt attempt, CancellationToken ct)
    {
        if (attempt.ReusedFrom is { } reuse)
            return attempts.Origin(attempt) is { } origin
                ? await GetAsync(origin, ct)
                : Note(attempt, $"reuses CI run #{reuse.Run}/{reuse.Step}@{reuse.Attempt}, which no longer exists");
        var identity = HarnessAttemptIdentity.Of(attempt);
        switch (attempt.Status)
        {
            case Scheduled:
                return Note(attempt, "is scheduled; no logs yet");
            case InProgress:
                return await liveLogs.TryGetAsync(identity, ct) ?? Note(attempt, "is starting; no logs yet");
        }

        if (await ReadAsync(identity.LogKey, ct) is { } stored)
            return stored;
        return attempt.Status switch
        {
            Skipped s when attempt.IsSchedulerSkip => Note(attempt, $"was skipped without running: {s.Reason}"),
            Cancelled { StartedAt: null } => Note(attempt, "was cancelled before it started"),
            Failed { Reason: { } reason } => Note(attempt, $"failed without a log: {reason}"),
            _ => Note(attempt, "has no log"),
        };
    }

    public async IAsyncEnumerable<ServerSentEvent<Contract.LogEvent>> StreamAsync(Id<CiAttempt> id, [EnumeratorCancellation] CancellationToken ct)
    {
        var sent = "";
        var idleSince = time.GetUtcNow();
        yield return new ServerSentEvent<Contract.LogEvent>(new Contract.LogChunk { Text = "", Reset = true });
        while (!ct.IsCancellationRequested)
        {
            if (!attempts.TryGet(id, out var attempt))
            {
                yield return new ServerSentEvent<Contract.LogEvent>(new Contract.LogStreamEnd { Status = Contract.LogEndStatus.Deleted });
                yield break;
            }

            var terminal = attempt.Status.IsTerminal();
            string? text;
            try
            {
                text = await GetAsync(attempt, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                text = null;
            }

            if (text is not null && text != sent)
            {
                yield return new ServerSentEvent<Contract.LogEvent>(text.StartsWith(sent, StringComparison.Ordinal)
                    ? new Contract.LogChunk { Text = text[sent.Length..], Reset = false }
                    : new Contract.LogChunk { Text = text, Reset = true });
                sent = text;
                idleSince = time.GetUtcNow();
            }

            if (terminal)
            {
                yield return new ServerSentEvent<Contract.LogEvent>(new Contract.LogStreamEnd { Status = attempt.Status.State().ToLogEnd() });
                yield break;
            }

            if (time.GetUtcNow() - idleSince >= SseStreams.HeartbeatInterval)
            {
                yield return new ServerSentEvent<Contract.LogEvent>(new Contract.LogPing());
                idleSince = time.GetUtcNow();
            }

            try
            {
                await Task.Delay(PollInterval, time, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = storageOptions.Bucket, Key = key }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string Note(CiAttempt attempt, string what) =>
        $"(attempt {attempt.Number} of step '{attempt.StepName}' in CI run #{attempt.RunNumber} {what})\n";
}

internal static class CiRunViewsExtensions
{
    public static NotFoundProblem NoRun(Id<Pipeline> pipelineId, int number) => new("CI run #{0} not found in pipeline '{1}'.", number, pipelineId);
}
