using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Processing.Api;

public static class ProcessingEndpoints
{
    public record CreateProcessingStepRequest(string Name);
    public record SetScriptProcessingRequest(string Script);
    public record CreateVerificationRequest(string Name);
    public record SetScriptVerificationRequest(string Script);

    public static void MapProcessingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/processing");

        group.MapPost("/", Result<ProcessingStep> (
            PipelineService pipelines,
            ProcessingStepService processing,
            Id<Pipeline> pipelineId,
            CreateProcessingStepRequest request) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<ProcessingStep>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var step = new ProcessingStep(Id.New<ProcessingStep>(), request.Name, pipelineId);
            processing.Set(step);
            return Result.Success(step);
        })
        .WithResultMapping<ProcessingStep>();

        group.MapGet("/{processingId}", Result<ProcessingStep> (
            ProcessingStepService processing,
            Id<ProcessingStep> processingId) =>
        {
            if (!processing.TryGet(processingId, out var step))
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
            Id<Pipeline> pipelineId) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<ProcessingStep[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var steps = processing.GetByPipelineId(pipelineId).ToArray();
            return Result.Success(steps);
        })
        .WithResultMapping<ProcessingStep[]>()
        .AllowAnonymous();

        group.MapDelete("/{processingId}", (
            ProcessingStepService processing,
            Id<ProcessingStep> processingId) =>
        {
            if (!processing.Delete(processingId))
            {
                return Result.Failure(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();

        // Script processing attachment
        group.MapPut("/{processingId}/script", Result<ScriptProcessing> (
            ProcessingStepService processing,
            Id<ProcessingStep> processingId,
            SetScriptProcessingRequest request) =>
        {
            if (!processing.TryGet(processingId, out _))
                return Result.Failure<ScriptProcessing>(new ResultProblem($"Processing step '{processingId}' not found."));

            var script = new ScriptProcessing(request.Script);
            processing.SetScript(processingId, script);
            return Result.Success(script);
        })
        .WithResultMapping<ScriptProcessing>();

        group.MapGet("/{processingId}/script", Result<ScriptProcessing> (
            ProcessingStepService processing,
            Id<ProcessingStep> processingId) =>
        {
            if (!processing.TryGetScript(processingId, out var script))
                return Result.Failure<ScriptProcessing>(new ResultProblem($"Processing step '{processingId}' has no script configuration."));

            return Result.Success(script);
        })
        .WithResultMapping<ScriptProcessing>()
        .AllowAnonymous();

        group.MapDelete("/{processingId}/script", (
            ProcessingStepService processing,
            Id<ProcessingStep> processingId) =>
        {
            if (!processing.RemoveScript(processingId))
                return Result.Failure(new ResultProblem($"Processing step '{processingId}' has no script configuration."));

            return Result.Success();
        })
        .WithResultMapping();

        // Verification endpoints
        var verificationGroup = app.MapGroup("/api/pipelines/{pipelineId}/processing/{processingId}/verifications");

        verificationGroup.MapPost("/", Result<Verification> (
            ProcessingStepService processing,
            VerificationService verifications,
            Id<ProcessingStep> processingId,
            CreateVerificationRequest request) =>
        {
            if (!processing.TryGet(processingId, out _))
            {
                return Result.Failure<Verification>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            var verification = new Verification(Id.New<Verification>(), request.Name, processingId);
            verifications.Set(verification);
            return Result.Success(verification);
        })
        .WithResultMapping<Verification>();

        verificationGroup.MapGet("/{verificationId}", Result<Verification> (
            VerificationService verifications,
            Id<Verification> verificationId) =>
        {
            if (!verifications.TryGet(verificationId, out var verification))
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
            Id<ProcessingStep> processingId) =>
        {
            if (!processing.TryGet(processingId, out _))
            {
                return Result.Failure<Verification[]>(new ResultProblem($"Processing step '{processingId}' not found."));
            }

            var steps = verifications.GetByProcessingStepId(processingId).ToArray();
            return Result.Success(steps);
        })
        .WithResultMapping<Verification[]>()
        .AllowAnonymous();

        verificationGroup.MapDelete("/{verificationId}", (
            VerificationService verifications,
            Id<Verification> verificationId) =>
        {
            if (!verifications.Delete(verificationId))
            {
                return Result.Failure(new ResultProblem($"Verification '{verificationId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();

        // Script verification attachment
        verificationGroup.MapPut("/{verificationId}/script", Result<ScriptVerification> (
            VerificationService verifications,
            Id<Verification> verificationId,
            SetScriptVerificationRequest request) =>
        {
            if (!verifications.TryGet(verificationId, out _))
                return Result.Failure<ScriptVerification>(new ResultProblem($"Verification '{verificationId}' not found."));

            var script = new ScriptVerification(request.Script);
            verifications.SetScript(verificationId, script);
            return Result.Success(script);
        })
        .WithResultMapping<ScriptVerification>();

        verificationGroup.MapGet("/{verificationId}/script", Result<ScriptVerification> (
            VerificationService verifications,
            Id<Verification> verificationId) =>
        {
            if (!verifications.TryGetScript(verificationId, out var script))
                return Result.Failure<ScriptVerification>(new ResultProblem($"Verification '{verificationId}' has no script configuration."));

            return Result.Success(script);
        })
        .WithResultMapping<ScriptVerification>()
        .AllowAnonymous();

        verificationGroup.MapDelete("/{verificationId}/script", (
            VerificationService verifications,
            Id<Verification> verificationId) =>
        {
            if (!verifications.RemoveScript(verificationId))
                return Result.Failure(new ResultProblem($"Verification '{verificationId}' has no script configuration."));

            return Result.Success();
        })
        .WithResultMapping();
    }
}
