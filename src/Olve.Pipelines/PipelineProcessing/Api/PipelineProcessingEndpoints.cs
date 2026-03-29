using System.Text.Json.Serialization;
using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineProcessing.Api;

public static class PipelineProcessingEndpoints
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(CreateScriptProcessingRequest), "script")]
    public abstract record CreatePipelineProcessingRequest(string Name);

    public record CreateScriptProcessingRequest(string Name, string Script) : CreatePipelineProcessingRequest(Name);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(CreateScriptVerificationRequest), "script")]
    public abstract record CreatePipelineVerificationRequest(string Name);

    public record CreateScriptVerificationRequest(string Name, string Script) : CreatePipelineVerificationRequest(Name);

    public static void MapPipelineProcessingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/processing");

        group.MapPost("/", Result<PipelineProcessingStep> (
            PipelineService pipelines,
            PipelineProcessingService processing,
            Guid pipelineId,
            CreatePipelineProcessingRequest request) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineProcessingStep>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var stepId = Id.New<PipelineProcessingStep>();

            var step = request switch
            {
                CreateScriptProcessingRequest script => (PipelineProcessingStep)new ScriptProcessingStep(stepId, script.Name, pipelineIdTyped, script.Script),
                _ => throw new InvalidOperationException("Unknown processing step type."),
            };

            processing.Set(step);
            return Result.Success(step);
        })
        .WithResultMapping<PipelineProcessingStep>();

        group.MapGet("/{processingId:guid}", Result<PipelineProcessingStep> (
            PipelineProcessingService processing,
            Guid processingId) =>
        {
            var processingIdTyped = new Id<PipelineProcessingStep>(new Id(processingId));

            if (!processing.TryGet(processingIdTyped, out var step))
            {
                return Result.Failure<PipelineProcessingStep>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            return Result.Success(step);
        })
        .WithResultMapping<PipelineProcessingStep>()
        .AllowAnonymous();

        group.MapGet("/", Result<PipelineProcessingStep[]> (
            PipelineService pipelines,
            PipelineProcessingService processing,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineProcessingStep[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var steps = processing.GetByPipelineId(pipelineIdTyped).ToArray();
            return Result.Success(steps);
        })
        .WithResultMapping<PipelineProcessingStep[]>()
        .AllowAnonymous();

        group.MapDelete("/{processingId:guid}", (
            PipelineProcessingService processing,
            Guid processingId) =>
        {
            var processingIdTyped = new Id<PipelineProcessingStep>(new Id(processingId));

            if (!processing.Delete(processingIdTyped))
            {
                return Result.Failure(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();

        // Verification endpoints
        var verificationGroup = app.MapGroup("/api/pipelines/{pipelineId:guid}/processing/{processingId:guid}/verifications");

        verificationGroup.MapPost("/", Result<PipelineVerification> (
            PipelineProcessingService processing,
            PipelineVerificationService verifications,
            Guid processingId,
            CreatePipelineVerificationRequest request) =>
        {
            var processingIdTyped = new Id<PipelineProcessingStep>(new Id(processingId));

            if (!processing.TryGet(processingIdTyped, out _))
            {
                return Result.Failure<PipelineVerification>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            var verification = new PipelineVerification(Id.New<PipelineVerification>(), request.Name, processingIdTyped);
            verifications.Set(verification);
            return Result.Success(verification);
        })
        .WithResultMapping<PipelineVerification>();

        verificationGroup.MapGet("/{verificationId:guid}", Result<PipelineVerification> (
            PipelineVerificationService verifications,
            Guid verificationId) =>
        {
            var verificationIdTyped = new Id<PipelineVerification>(new Id(verificationId));

            if (!verifications.TryGet(verificationIdTyped, out var verification))
            {
                return Result.Failure<PipelineVerification>(new ResultProblem($"Verification '{verificationId}' not found."));
            }

            return Result.Success(verification);
        })
        .WithResultMapping<PipelineVerification>()
        .AllowAnonymous();

        verificationGroup.MapGet("/", Result<PipelineVerification[]> (
            PipelineProcessingService processing,
            PipelineVerificationService verifications,
            Guid processingId) =>
        {
            var processingIdTyped = new Id<PipelineProcessingStep>(new Id(processingId));

            if (!processing.TryGet(processingIdTyped, out _))
            {
                return Result.Failure<PipelineVerification[]>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            var steps = verifications.GetByProcessingId(processingIdTyped).ToArray();
            return Result.Success(steps);
        })
        .WithResultMapping<PipelineVerification[]>()
        .AllowAnonymous();

        verificationGroup.MapDelete("/{verificationId:guid}", (
            PipelineVerificationService verifications,
            Guid verificationId) =>
        {
            var verificationIdTyped = new Id<PipelineVerification>(new Id(verificationId));

            if (!verifications.Delete(verificationIdTyped))
            {
                return Result.Failure(new ResultProblem($"Verification '{verificationId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();
    }
}
