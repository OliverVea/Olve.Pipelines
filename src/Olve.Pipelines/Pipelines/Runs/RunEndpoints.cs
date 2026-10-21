using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Olve.MinimalApi;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>What a trigger request did: <c>started</c> (with the run), <c>deduplicated</c> (with the
/// existing run) or <c>queued</c> (the pipeline is switching versions; it starts when the switch completes).</summary>
public record TriggerRunResponse(string Outcome, Run? Run);

/// <summary>One attempt's output, addressed <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@&lt;attempt&gt;]</c> (spec §5.5).</summary>
/// <param name="Output">The output JSON (null for a step without an output model).</param>
public record AttemptOutputResponse(int Run, string Step, int Attempt, string Status, JsonElement? Output, string? OutputKey);

/// <summary>Minimal run endpoints for M1 (trigger, runs, attempts, logs, outputs). The full API is B8.</summary>
public static class RunEndpoints
{
    public static void MapRunEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/runs");

        group.MapGet("/", Result<Run[]> (RunService service, Id<Pipeline> pipelineId)
                => service.ListByPipeline(pipelineId).ToArray())
            .WithResultMapping<Run[]>()
            .WithName("ListRuns")
            .AllowAnonymous();

        // Manual trigger: the body is the pipeline input (the input model's z.input view; defaults are
        // filled in). Invalid input is a 400 listing every problem — never a half-started run.
        group.MapPost("/", Result<TriggerRunResponse> (RunInputService inputs, Id<Pipeline> pipelineId, [FromBody] JsonElement input)
                => inputs.StartManual(pipelineId, System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText()))
                    .Map(outcome => outcome switch
                    {
                        RunStartOutcome.Started s => new TriggerRunResponse("started", s.Run),
                        RunStartOutcome.Deduplicated d => new TriggerRunResponse("deduplicated", d.Existing),
                        _ => new TriggerRunResponse("queued", null),
                    }))
            .WithResultMapping<TriggerRunResponse>()
            .WithName("TriggerRun");

        group.MapGet("/{number:int}", Result<Run> (RunService service, Id<Pipeline> pipelineId, int number)
                => service.TryGetByNumber(pipelineId, number, out var run)
                    ? run
                    : new ResultProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId))
            .WithResultMapping<Run>()
            .WithName("GetRun")
            .AllowAnonymous();

        group.MapGet("/{number:int}/attempts", Result<Attempt[]> (RunService runs, AttemptService attempts, Id<Pipeline> pipelineId, int number)
                => runs.TryGetByNumber(pipelineId, number, out var run)
                    ? attempts.ListByRun(run.Id).ToArray()
                    : new ResultProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId))
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
}
