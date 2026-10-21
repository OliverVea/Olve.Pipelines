using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Olve.MinimalApi;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Utilities.Paginations;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>What a trigger request did: <c>started</c> (with the run), <c>deduplicated</c> (with the
/// existing run) or <c>queued</c> (the pipeline is switching versions; it starts when the switch completes).</summary>
public record TriggerRunResponse(string Outcome, Run? Run);

/// <summary>One attempt's output, addressed <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@&lt;attempt&gt;]</c> (spec §5.5).</summary>
/// <param name="Output">The output JSON (null for a step without an output model).</param>
public record AttemptOutputResponse(int Run, string Step, int Attempt, string Status, JsonElement? Output, string? OutputKey);

/// <summary>Run API (spec §5): trigger, list/get/cancel/rerun/continue runs, attempts, step reruns, outputs, files and logs.</summary>
public static class RunEndpoints
{
    public static void MapRunEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/runs");

        // Newest first, paged.
        group.MapGet("/", Result<Page<RunSummary>> (RunViewService views, Id<Pipeline> pipelineId, [AsParameters] ListRunsRequest request)
                => views.List(pipelineId, request))
            .WithResultMapping<Page<RunSummary>>()
            .WithName("ListRuns")
            .AllowAnonymous();

        // Manual trigger: the body is the pipeline input (the input model's z.input view; defaults are
        // filled in). Invalid input is a 400 listing every problem — never a half-started run.
        group.MapPost("/", Result<TriggerRunResponse> (RunInputService inputs, Id<Pipeline> pipelineId, [FromBody] TriggerRunRequest input)
                => inputs.StartManual(pipelineId, JsonFields.ToJsonObject(input.Fields))
                    .Map(ToResponse))
            .WithResultMapping<TriggerRunResponse>()
            .WithName("TriggerRun");

        // Input, pinned version, links, and the pinned manifest's stage layout with each step's latest attempt.
        group.MapGet("/{number:int}", Result<RunDetail> (RunViewService views, Id<Pipeline> pipelineId, int number)
                => views.Get(pipelineId, number))
            .WithResultMapping<RunDetail>()
            .WithName("GetRun")
            .AllowAnonymous();

        // Cancels the run and its scheduled/in-progress attempts (their K8s Jobs are deleted).
        group.MapPost("/{number:int}/cancel", Result<Run> (RunService runs, RunCancellationService cancellation, Id<Pipeline> pipelineId, int number)
                => FindRun(runs, pipelineId, number).Bind(run => cancellation.Cancel(run.Id)))
            .WithResultMapping<Run>()
            .WithName("CancelRun");

        // Whole-run rerun: a new run with the same input on the active version, linked via rerunOf.
        group.MapPost("/{number:int}/rerun", Result<TriggerRunResponse> (RunService runs, RunInputService inputs, Id<Pipeline> pipelineId, int number)
                => FindRun(runs, pipelineId, number).Bind(run => inputs.RerunRun(run)).Map(ToResponse))
            .WithResultMapping<TriggerRunResponse>()
            .WithName("RerunRun");

        // Continue: keeps completed stages, re-evaluates the rest; optional input changes are merged over the
        // stored input (only fields no completed step consumed may change).
        group.MapPost("/{number:int}/continue", Result<Run> (
                RunService runs, RunInputService inputs, Id<Pipeline> pipelineId, int number, [FromBody] ContinueRunRequest? request)
                => FindRun(runs, pipelineId, number).Bind(run => inputs.Continue(run, JsonFields.ToJsonObject(request?.Input))))
            .WithResultMapping<Run>()
            .WithName("ContinueRun");

        group.MapGet("/{number:int}/steps/{step}/attempts", Result<Attempt[]> (
                RunService runs, AttemptService attempts, Id<Pipeline> pipelineId, int number, string step)
                => FindRun(runs, pipelineId, number).Map(run => attempts.ListForStep(run.Id, step).ToArray()))
            .WithResultMapping<Attempt[]>()
            .WithName("ListStepAttempts")
            .AllowAnonymous();

        // A new attempt of one step (same pinned version, current pool input); continue=true re-opens the run downstream.
        group.MapPost("/{number:int}/steps/{step}/rerun", Result<Attempt> (
                RunService runs, RunContinuationService continuations, Id<Pipeline> pipelineId, int number, string step,
                [FromBody] RerunStepRequest? request)
                => FindRun(runs, pipelineId, number).Bind(run => continuations.RerunStep(run.Id, step, request?.Continue ?? false)))
            .WithResultMapping<Attempt>()
            .WithName("RerunStep");

        // The stored files of an attempt (without ?attempt= the latest successful one).
        group.MapGet("/{number:int}/steps/{step}/files", async Task<Result<AttemptFilesResponse>> (
                RunQueryService query, AttemptFileService files, Id<Pipeline> pipelineId, int number, string step, int? attempt,
                CancellationToken ct) =>
            {
                if (!query.TryResolve(pipelineId, number, step, attempt, out var resolved))
                    return NoAttempt(number, step, attempt);
                return await files.ListAsync(resolved, ct);
            })
            .WithResultMapping<AttemptFilesResponse>()
            .WithName("ListStepFiles");

        // Downloads one file by its §5.5 address #run/step[@attempt]/path (a directory ref "dir/" yields dir.tar).
        group.MapGet("/{number:int}/steps/{step}/files/{*path}", async Task<IResult> (
                HttpContext http, RunQueryService query, AttemptFileService files, Id<Pipeline> pipelineId, int number, string step,
                string path, int? attempt, CancellationToken ct) =>
            {
                if (AttemptFileService.ToObjectPath(path).TryPickProblems(out var pathProblems, out var objectPath))
                    return Problems(pathProblems, StatusCodes.Status400BadRequest);
                if (!query.TryResolve(pipelineId, number, step, attempt, out var resolved))
                    return Problems([NoAttempt(number, step, attempt)], StatusCodes.Status404NotFound);

                var download = await files.OpenAsync(resolved, objectPath, ct);
                if (download is null)
                    return Problems([new ResultProblem("Attempt {0} of step '{1}' in run #{2} has no file '{3}'.",
                        resolved.Number, step, number, objectPath)], StatusCodes.Status404NotFound);

                http.Response.RegisterForDispose(download);
                if (download.Length is { } length)
                    http.Response.ContentLength = length;
                return Microsoft.AspNetCore.Http.Results.Stream(download.Content, download.ContentType, download.FileName);
            })
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces<ResultProblem[]>(StatusCodes.Status400BadRequest)
            .Produces<ResultProblem[]>(StatusCodes.Status404NotFound)
            .WithName("GetStepFile");

        group.MapGet("/{number:int}/attempts", Result<Attempt[]> (RunService runs, AttemptService attempts, Id<Pipeline> pipelineId, int number)
                => FindRun(runs, pipelineId, number).Map(run => attempts.ListByRun(run.Id).ToArray()))
            .WithResultMapping<Attempt[]>()
            .WithName("ListRunAttempts")
            .AllowAnonymous();

        // Without ?attempt= the output of the latest successful attempt (what later stages consumed).
        group.MapGet("/{number:int}/steps/{step}/output", Result<AttemptOutputResponse> (
                RunQueryService query, Id<Pipeline> pipelineId, int number, string step, int? attempt)
                => query.TryResolve(pipelineId, number, step, attempt, out var resolved)
                    ? new AttemptOutputResponse(number, step, resolved.Number, resolved.Status.Discriminator(),
                        resolved.Output is { } o ? JsonDocument.Parse(o.Json).RootElement.Clone() : null, resolved.Output?.Key)
                    : new ResultProblem(attempt is null
                        ? "Run #{0} has no successful attempt of step '{1}'."
                        : "Run #{0} has no attempt {2} of step '{1}'.", number, step, attempt ?? 0))
            .WithResultMapping<AttemptOutputResponse>()
            .WithName("GetStepOutput")
            .AllowAnonymous();

        // Without ?attempt= the latest attempt (whatever its status).
        group.MapGet("/{number:int}/steps/{step}/logs", async Task<Result<string>> (
                RunService runs, AttemptService attempts, JobLogService logs, Id<Pipeline> pipelineId, int number, string step, int? attempt,
                CancellationToken ct) =>
            {
                if (!runs.TryGetByNumber(pipelineId, number, out var run))
                    return new ResultProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId);
                var target = attempt is { } n
                    ? attempts.ListForStep(run.Id, step).FirstOrDefault(a => a.Number == n)
                    : attempts.ListForStep(run.Id, step).LastOrDefault();
                if (target is null)
                    return new ResultProblem("Run #{0} has no such attempt of step '{1}'.", number, step);
                return await logs.GetLogsAsync(target.Id, ct);
            })
            .WithResultMapping<string>()
            .WithName("GetStepLogs")
            .AllowAnonymous();
    }

    private static Result<Run> FindRun(RunService runs, Id<Pipeline> pipelineId, int number)
        => runs.TryGetByNumber(pipelineId, number, out var run)
            ? run
            : new ResultProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId);

    private static TriggerRunResponse ToResponse(RunStartOutcome outcome) => outcome switch
    {
        RunStartOutcome.Started s => new TriggerRunResponse("started", s.Run),
        RunStartOutcome.Deduplicated d => new TriggerRunResponse("deduplicated", d.Existing),
        _ => new TriggerRunResponse("queued", null),
    };

    private static ResultProblem NoAttempt(int number, string step, int? attempt) => new(attempt is null
        ? "Run #{0} has no successful attempt of step '{1}'."
        : "Run #{0} has no attempt {2} of step '{1}'.", number, step, attempt ?? 0);

    private static IResult Problems(IEnumerable<ResultProblem> problems, int statusCode)
        => Microsoft.AspNetCore.Http.Results.Json(problems.ToArray(), AppJsonContext.Default.ResultProblemArray, statusCode: statusCode);
}
