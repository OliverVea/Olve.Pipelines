using System.Text.Json;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Shared.Problems;
using Olve.Pipelines.Streaming;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Runs;

public sealed class ListStepAttemptsHandler(RunService runs, AttemptService attempts) : Contract.IRunStepsAttemptsHandler
{
    public Task<Contract.RunStepsAttemptsResponse> HandleAsync(Contract.RunStepsAttemptsRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.RunStepsAttemptsResponse>(RunViews.Find(runs, id, request.Number).TryPickProblems(out var problems, out var run)
            ? new Contract.RunStepsAttemptsResponse.NotFound(ApiProblems.Of([.. problems]))
            : new Contract.RunStepsAttemptsResponse.Ok([.. attempts.ListForStep(run.Id, request.Step).Select(RunViews.ToApi)]));
    }
}

/// <summary>A new attempt of one step (same pinned version, current pool input); continue re-opens the run downstream.</summary>
public sealed class RerunStepHandler(RunService runs, RunContinuationService continuations) : Contract.IRunStepsRerunHandler
{
    public Task<Contract.RunStepsRerunResponse> HandleAsync(Contract.RunStepsRerunRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var rerun = RunViews.Find(runs, id, request.Number).Bind(run => continuations.RerunStep(run.Id, request.Step, request.Body.Continue ?? false));
        return Task.FromResult(rerun.TryPickProblems(out var problems, out var attempt)
            ? RunViews.Fail<Contract.RunStepsRerunResponse>(problems, p => new Contract.RunStepsRerunResponse.NotFound(p), p => new Contract.RunStepsRerunResponse.Conflict(p), p => new Contract.RunStepsRerunResponse.BadRequest(p))
            : new Contract.RunStepsRerunResponse.Ok(RunViews.ToApi(attempt)));
    }
}

/// <summary>
/// Cancels the step's scheduled/in-progress attempt (its K8s Job is deleted). Like any cancelled attempt, the stage's
/// other steps keep running and the run ends Cancelled at the stage barrier; rerun the step with continue to re-open it.
/// </summary>
public sealed class CancelStepHandler(RunService runs, RunCancellationService cancellation) : Contract.IRunStepsCancelHandler
{
    public Task<Contract.RunStepsCancelResponse> HandleAsync(Contract.RunStepsCancelRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var cancelled = RunViews.Find(runs, id, request.Number).Bind(run => cancellation.CancelStep(run.Id, request.Step));
        return Task.FromResult(cancelled.TryPickProblems(out var problems, out var attempt)
            ? RunViews.Fail<Contract.RunStepsCancelResponse>(problems, p => new Contract.RunStepsCancelResponse.NotFound(p), p => new Contract.RunStepsCancelResponse.Conflict(p), p => new Contract.RunStepsCancelResponse.BadRequest(p))
            : new Contract.RunStepsCancelResponse.Ok(RunViews.ToApi(attempt)));
    }
}

public sealed class AnswerStepInputHandler(ExternalInputService inputs, IHttpContextAccessor http) : Contract.IRunStepsAnswerHandler
{
    public Task<Contract.RunStepsAnswerResponse> HandleAsync(Contract.RunStepsAnswerRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var answered = inputs.Answer(id, request.Number, request.Step, request.Body.Name, request.Body.Value, SecretViews.Actor(http.HttpContext?.User));
        return Task.FromResult(answered.TryPickProblems(out var problems, out var attempt)
            ? RunViews.Fail<Contract.RunStepsAnswerResponse>(problems, p => new Contract.RunStepsAnswerResponse.NotFound(p), p => new Contract.RunStepsAnswerResponse.Conflict(p), p => new Contract.RunStepsAnswerResponse.BadRequest(p))
            : new Contract.RunStepsAnswerResponse.Ok(RunViews.ToApi(attempt)));
    }
}

public sealed class DeclineStepInputHandler(ExternalInputService inputs, IHttpContextAccessor http) : Contract.IRunStepsDeclineHandler
{
    public Task<Contract.RunStepsDeclineResponse> HandleAsync(Contract.RunStepsDeclineRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var declined = inputs.Decline(id, request.Number, request.Step, request.Body.Comment, SecretViews.Actor(http.HttpContext?.User));
        return Task.FromResult(declined.TryPickProblems(out var problems, out var attempt)
            ? RunViews.Fail<Contract.RunStepsDeclineResponse>(problems, p => new Contract.RunStepsDeclineResponse.NotFound(p), p => new Contract.RunStepsDeclineResponse.Conflict(p), p => new Contract.RunStepsDeclineResponse.BadRequest(p))
            : new Contract.RunStepsDeclineResponse.Ok(RunViews.ToApi(attempt)));
    }
}

/// <summary>The stored files of an attempt (without <c>attempt</c> the latest successful one).</summary>
public sealed class ListStepFilesHandler(RunQueryService query, AttemptFileService files) : Contract.IRunStepsFilesHandler
{
    public async Task<Contract.RunStepsFilesResponse> HandleAsync(Contract.RunStepsFilesRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!query.TryResolve(id, request.Number, request.Step, request.Attempt, out var resolved))
            return new Contract.RunStepsFilesResponse.NotFound(ApiProblems.Of(NoAttempt(request.Number, request.Step, request.Attempt)));
        return new Contract.RunStepsFilesResponse.Ok(RunViews.ToApi(await files.ListAsync(resolved, cancellationToken)));
    }

    public static NotFoundProblem NoAttempt(int number, string step, int? attempt) => new(attempt is null
        ? "Run #{0} has no successful attempt of step '{1}'."
        : "Run #{0} has no attempt {2} of step '{1}'.", number, step, attempt ?? 0);
}

/// <summary>One stored file by its address <c>#run/step[@attempt]/path</c>; a directory ref <c>dir/</c> downloads as <c>dir.tar</c>.</summary>
public sealed class DownloadStepFileHandler(RunQueryService query, AttemptFileService files, IHttpContextAccessor http) : Contract.IRunStepsDownloadHandler
{
    public async Task<Contract.RunStepsDownloadResponse> HandleAsync(Contract.RunStepsDownloadRequest request, CancellationToken cancellationToken)
    {
        if (AttemptFileService.ToObjectPath(request.Path).TryPickProblems(out var pathProblems, out var objectPath))
            return RunViews.Fail<Contract.RunStepsDownloadResponse>(pathProblems,
                p => new Contract.RunStepsDownloadResponse.NotFound(p), null, p => new Contract.RunStepsDownloadResponse.BadRequest(p));
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!query.TryResolve(id, request.Number, request.Step, request.Attempt, out var resolved))
            return new Contract.RunStepsDownloadResponse.NotFound(ApiProblems.Of(ListStepFilesHandler.NoAttempt(request.Number, request.Step, request.Attempt)));

        var download = await files.OpenAsync(resolved, objectPath, cancellationToken);
        if (download is null)
            return new Contract.RunStepsDownloadResponse.NotFound(ApiProblems.Of(new NotFoundProblem(
                "Attempt {0} of step '{1}' in run #{2} has no file '{3}'.", resolved.Number, request.Step, request.Number, objectPath)));

        // Disposing the content stream alone doesn't release the S3 response.
        http.HttpContext?.Response.RegisterForDispose(download);
        return new Contract.RunStepsDownloadResponse.Ok(download.Content, download.ContentType, Attachment(download.FileName), download.Length);
    }

    /// <summary>An ASCII <c>filename</c> and an encoded <c>filename*</c>, so any file name is a valid header.</summary>
    public static string Attachment(string fileName)
    {
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        return disposition.ToString();
    }
}

/// <summary>Without <c>attempt</c> the output of the latest successful attempt (what later stages consumed).</summary>
public sealed class GetStepOutputHandler(RunQueryService query) : Contract.IRunStepsOutputHandler
{
    public Task<Contract.RunStepsOutputResponse> HandleAsync(Contract.RunStepsOutputRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!query.TryResolve(id, request.Number, request.Step, request.Attempt, out var resolved))
            return Task.FromResult<Contract.RunStepsOutputResponse>(new Contract.RunStepsOutputResponse.NotFound(
                ApiProblems.Of(ListStepFilesHandler.NoAttempt(request.Number, request.Step, request.Attempt))));
        return Task.FromResult<Contract.RunStepsOutputResponse>(new Contract.StepOutput
        {
            Run = request.Number,
            Step = request.Step,
            Attempt = resolved.Number,
            Status = resolved.Status.Discriminator(),
            Output = resolved.Output is { } o ? JsonDocument.Parse(o.Json).RootElement.Clone() : null,
            OutputKey = resolved.Output?.Key,
        });
    }
}

/// <summary>Without <c>attempt</c> the latest attempt (whatever its status).</summary>
public sealed class GetStepLogsHandler(RunService runs, AttemptService attempts, JobLogService logs) : Contract.IRunStepsLogsHandler
{
    public async Task<Contract.RunStepsLogsResponse> HandleAsync(Contract.RunStepsLogsRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!runs.TryGetByNumber(id, request.Number, out var run))
            return new Contract.RunStepsLogsResponse.NotFound(ApiProblems.Of(new NotFoundProblem("Run #{0} not found in pipeline '{1}'.", request.Number, id)));
        var target = request.Attempt is { } n
            ? attempts.ListForStep(run.Id, request.Step).FirstOrDefault(a => a.Number == n)
            : attempts.ListForStep(run.Id, request.Step).LastOrDefault();
        if (target is null)
            return new Contract.RunStepsLogsResponse.NotFound(ApiProblems.Of(new NotFoundProblem("Run #{0} has no such attempt of step '{1}'.", request.Number, request.Step)));
        // The status is read before the log: a finished attempt's log can't grow after it.
        var complete = target.Status.IsTerminal();
        var text = await logs.GetLogsAsync(target.Id, cancellationToken);
        return text.TryPickProblems(out var problems, out var value)
            ? new Contract.RunStepsLogsResponse.NotFound(ApiProblems.Of([.. problems]))
            : new Contract.StepLog
            {
                Run = request.Number,
                Step = request.Step,
                Attempt = target.Number,
                Status = target.Status.Discriminator(),
                Complete = complete,
                Text = value,
            };
    }
}

/// <summary>Without <c>attempt</c> the latest attempt. Ends with <c>end</c> once the attempt is terminal.</summary>
public sealed class StreamStepLogsHandler(RunService runs, AttemptService attempts, AttemptLogStreamer streamer) : Contract.IRunStepsLogStreamHandler
{
    public Task<Contract.RunStepsLogStreamResponse> HandleAsync(Contract.RunStepsLogStreamRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!runs.TryGetByNumber(id, request.Number, out var run))
            return Task.FromResult<Contract.RunStepsLogStreamResponse>(new Contract.RunStepsLogStreamResponse.NotFound(
                ApiProblems.Of(new NotFoundProblem("Run #{0} not found in pipeline '{1}'.", request.Number, id))));
        var target = request.Attempt is { } n
            ? attempts.ListForStep(run.Id, request.Step).FirstOrDefault(a => a.Number == n)
            : attempts.ListForStep(run.Id, request.Step).LastOrDefault();
        if (target is null)
            return Task.FromResult<Contract.RunStepsLogStreamResponse>(new Contract.RunStepsLogStreamResponse.NotFound(
                ApiProblems.Of(new NotFoundProblem("Run #{0} has no such attempt of step '{1}'.", request.Number, request.Step))));
        return Task.FromResult<Contract.RunStepsLogStreamResponse>(new Contract.RunStepsLogStreamResponse.Ok(streamer.StreamAsync(target.Id, cancellationToken)));
    }
}
