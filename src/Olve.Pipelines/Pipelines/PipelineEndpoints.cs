using Olve.MinimalApi;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public static class PipelineEndpoints
{
    public static void MapPipelineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines");

        // Pipelines are created already bound to a repo via POST /api/pipelines/with-repo
        // (PipelineBindingEndpoints). GitOps is the only writer of pipeline shape, so there is no
        // bare, unbound create — a pipeline without a binding would be a permanently empty shell.

        group.MapGet("/{id}", Result<Pipeline> (PipelineService service, Id<Pipeline> id)
                => service.TryGet(id, out var pipeline)
                    ? Result.Success(pipeline)
                    : Result.Failure<Pipeline>(new ResultProblem($"Pipeline '{id}' not found.")))
            .WithResultMapping<Pipeline>()
            .WithName("GetPipeline")
            .AllowAnonymous();

        group.MapGet("/", Result<Pipeline[]> (PipelineService service)
                => service.List().ToArray())
            .WithResultMapping<Pipeline[]>()
            .WithName("ListPipelines")
            .AllowAnonymous();

        group.MapGet("/summary", Result<PipelineSummary[]> (PipelineSummaryService service)
                => service.List().ToArray())
            .WithResultMapping<PipelineSummary[]>()
            .WithName("ListPipelineSummaries")
            .AllowAnonymous();

        group.MapDelete("/{id}", DeletionResult (PipelineService service, Id<Pipeline> id)
                => service.Delete(id))
            .WithDeletionMapping()
            .WithName("DeletePipeline");

        group.MapPost("/{id}/trigger/production", Result<JobGroup> (
            PipelineService pipelines,
            ProductionStepService productionSteps,
            ArtifactBundleService bundles,
            JobGroupService jobGroups,
            JobService jobs,
            Id<Pipeline> id) =>
            {
                if (!pipelines.TryGet(id, out _))
                    return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{id}' not found."));

                if (!productionSteps.HasConfiguredSteps(id))
                    return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{id}' has no configured production steps."));

                var steps = productionSteps.GetByPipelineId(id);
                if (steps.TryPickProblems(out var problems, out var stepArray))
                    return problems;

                var bundle = bundles.Create(id, ArtifactBundleStatus.Pending);
                var jobGroup = jobGroups.CreateProductionGroup(id, bundle.Id);

                foreach (var step in stepArray)
                {
                    _ = jobs.CreateProductionJob(id, jobGroup.Id, step.Id); // store-only create, cannot fail
                }

                return jobGroup;
            })
            .WithResultMapping<JobGroup>()
            .WithName("TriggerProduction");

        group.MapPost("/{id}/trigger/processing", Result<JobGroup> (
            PipelineService pipelines,
            ProcessingStepService processingSteps,
            PromotionGateService promotionGate,
            ArtifactBundleService bundles,
            JobService jobs,
            Id<Pipeline> id,
            TriggerProcessingRequest request) =>
            {
                if (!pipelines.TryGet(id, out _))
                    return Result.Failure<JobGroup>(new ResultProblem($"Pipeline '{id}' not found."));

                if (!bundles.TryGet(request.ArtifactBundleId, out var bundle))
                    return Result.Failure<JobGroup>(new ResultProblem($"Artifact bundle '{request.ArtifactBundleId}' not found."));

                var stepResult = processingSteps.TryGet(request.ProcessingStepId);
                if (stepResult.TryPickProblems(out var problems, out var step))
                    return problems;

                if (step.PipelineId != id)
                    return Result.Failure<JobGroup>(new ResultProblem($"Processing step '{request.ProcessingStepId}' does not belong to pipeline '{id}'."));

                if (promotionGate.IsBlocked(step.Id))
                    return Result.Failure<JobGroup>(new ResultProblem($"Processing step '{step.Id}' has promotion blocked; trigger refused."));

                var configResult = processingSteps.TryGetConfiguration(step.Id);
                if (configResult.TryPickProblems(out problems))
                    return problems;

                return jobs.CreateProcessingRun(id, bundle.Id, step.Id);
            })
            .WithResultMapping<JobGroup>()
            .WithName("TriggerProcessing");
    }
}

public record TriggerProcessingRequest(Id<ArtifactBundle> ArtifactBundleId, Id<ProcessingStep> ProcessingStepId);
