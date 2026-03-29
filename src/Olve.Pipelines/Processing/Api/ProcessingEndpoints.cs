using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Processing.Api;

public static class ProcessingEndpoints
{
    public record CreateProcessingStepRequest(string Name);
    public record CreateVerificationRequest(string Name);

    public static void MapProcessingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/processing");

        group.MapPost("/", Result<ProcessingStep> (
            PipelineService pipelines,
            ProcessingStepService processing,
            Guid pipelineId,
            CreateProcessingStepRequest request) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<ProcessingStep>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var step = new ProcessingStep(Id.New<ProcessingStep>(), request.Name, pipelineIdTyped);
            processing.Set(step);
            return Result.Success(step);
        })
        .WithResultMapping<ProcessingStep>();

        group.MapGet("/{processingId:guid}", Result<ProcessingStep> (
            ProcessingStepService processing,
            Guid processingId) =>
        {
            var processingIdTyped = new Id<ProcessingStep>(new Id(processingId));

            if (!processing.TryGet(processingIdTyped, out var step))
            {
                return Result.Failure<ProcessingStep>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            return Result.Success(step);
        })
        .WithResultMapping<ProcessingStep>()
        .AllowAnonymous();

        group.MapGet("/", Result<ProcessingStep[]> (
            PipelineService pipelines,
            ProcessingStepService processing,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<ProcessingStep[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var steps = processing.GetByPipelineId(pipelineIdTyped).ToArray();
            return Result.Success(steps);
        })
        .WithResultMapping<ProcessingStep[]>()
        .AllowAnonymous();

        group.MapDelete("/{processingId:guid}", (
            ProcessingStepService processing,
            Guid processingId) =>
        {
            var processingIdTyped = new Id<ProcessingStep>(new Id(processingId));

            if (!processing.Delete(processingIdTyped))
            {
                return Result.Failure(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();

        // Verification endpoints
        var verificationGroup = app.MapGroup("/api/pipelines/{pipelineId:guid}/processing/{processingId:guid}/verifications");

        verificationGroup.MapPost("/", Result<Verification> (
            ProcessingStepService processing,
            VerificationService verifications,
            Guid processingId,
            CreateVerificationRequest request) =>
        {
            var processingIdTyped = new Id<ProcessingStep>(new Id(processingId));

            if (!processing.TryGet(processingIdTyped, out _))
            {
                return Result.Failure<Verification>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            var verification = new Verification(Id.New<Verification>(), request.Name, processingIdTyped);
            verifications.Set(verification);
            return Result.Success(verification);
        })
        .WithResultMapping<Verification>();

        verificationGroup.MapGet("/{verificationId:guid}", Result<Verification> (
            VerificationService verifications,
            Guid verificationId) =>
        {
            var verificationIdTyped = new Id<Verification>(new Id(verificationId));

            if (!verifications.TryGet(verificationIdTyped, out var verification))
            {
                return Result.Failure<Verification>(new ResultProblem($"Verification '{verificationId}' not found."));
            }

            return Result.Success(verification);
        })
        .WithResultMapping<Verification>()
        .AllowAnonymous();

        verificationGroup.MapGet("/", Result<Verification[]> (
            ProcessingStepService processing,
            VerificationService verifications,
            Guid processingId) =>
        {
            var processingIdTyped = new Id<ProcessingStep>(new Id(processingId));

            if (!processing.TryGet(processingIdTyped, out _))
            {
                return Result.Failure<Verification[]>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            var steps = verifications.GetByProcessingStepId(processingIdTyped).ToArray();
            return Result.Success(steps);
        })
        .WithResultMapping<Verification[]>()
        .AllowAnonymous();

        verificationGroup.MapDelete("/{verificationId:guid}", (
            VerificationService verifications,
            Guid verificationId) =>
        {
            var verificationIdTyped = new Id<Verification>(new Id(verificationId));

            if (!verifications.Delete(verificationIdTyped))
            {
                return Result.Failure(new ResultProblem($"Verification '{verificationId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();
    }
}
