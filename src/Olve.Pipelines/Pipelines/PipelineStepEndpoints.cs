using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Pipeline-scoped step and health endpoints: promotion gates (spec §5.3, keyed on step name and kept
/// across versions), step history by identity (spec §6.3) and health over runs (spec §7).
/// </summary>
public static class PipelineStepEndpoints
{
    public static void MapPipelineStepEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}");

        group.MapGet("/health", Result<PipelineHealth> (PipelineService pipelines, PipelineHealthService health, Id<Pipeline> pipelineId)
                => pipelines.TryGet(pipelineId, out _)
                    ? health.Get(pipelineId)
                    : new NotFoundProblem("Pipeline '{0}' not found.", pipelineId))
            .WithApiResultMapping<PipelineHealth>()
            .WithName("GetPipelineHealth")
            .AllowAnonymous();

        group.MapGet("/gates", Result<string[]> (StepGateService gates, Id<Pipeline> pipelineId)
                => gates.ListBlocked(pipelineId).ToArray())
            .WithApiResultMapping<string[]>()
            .WithName("ListBlockedSteps")
            .AllowAnonymous();

        // Blocking holds runs at the step (no attempt is created); unblocking resumes held runs.
        group.MapPost("/steps/{step}/block", Result<string[]> (StepGateService gates, Id<Pipeline> pipelineId, string step)
                => ChangeGate(gates, pipelineId, step, block: true))
            .WithApiResultMapping<string[]>()
            .WithName("BlockStep");

        group.MapPost("/steps/{step}/unblock", Result<string[]> (StepGateService gates, Id<Pipeline> pipelineId, string step)
                => ChangeGate(gates, pipelineId, step, block: false))
            .WithApiResultMapping<string[]>()
            .WithName("UnblockStep");

        group.MapGet("/steps/{step}/history", Result<StepHistoryItem[]> (RunViewService views, Id<Pipeline> pipelineId, string step, int? limit)
                => views.History(pipelineId, step, Math.Clamp(limit ?? 50, 1, 500)).ToArray())
            .WithApiResultMapping<StepHistoryItem[]>()
            .WithName("GetStepHistory")
            .AllowAnonymous();
    }

    private static Result<string[]> ChangeGate(StepGateService gates, Id<Pipeline> pipelineId, string step, bool block)
    {
        if (!ManifestValidator.IsValidStepName(step))
            return ValidationProblem.ForField("step", "'{0}' is not a valid step name.", step);

        var changed = block ? gates.Block(pipelineId, step) : gates.Unblock(pipelineId, step);
        return changed.TryPickProblems(out var problems)
            ? Result.Failure<string[]>(problems)
            : gates.ListBlocked(pipelineId).ToArray();
    }
}
