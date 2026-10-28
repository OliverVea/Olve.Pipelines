using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Shared.Problems;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines;

public sealed class ListBlockedStepsHandler(PipelineService pipelines, StepBlockService blocks) : Contract.IStepsBlockedHandler
{
    public Task<Contract.StepsBlockedResponse> HandleAsync(Contract.StepsBlockedRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.StepsBlockedResponse>(pipelines.TryGet(id, out _)
            ? new Contract.StepsBlockedResponse.Ok(blocks.ListBlocked(id))
            : new Contract.StepsBlockedResponse.NotFound(PipelineViews.NotFound(id)));
    }
}

/// <summary>Blocking holds runs at the step (no attempt is created); unblocking resumes held runs.</summary>
public sealed class BlockStepHandler(StepBlockService blocks) : Contract.IStepsBlockHandler
{
    public Task<Contract.StepsBlockResponse> HandleAsync(Contract.StepsBlockRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (StepViews.ChangeBlock(blocks, id, request.Step, block: true).TryPickProblems(out var problems, out var blocked))
        {
            var problem = ApiProblems.Of([.. problems]);
            return Task.FromResult<Contract.StepsBlockResponse>(problem.Status == StatusCodes.Status404NotFound
                ? new Contract.StepsBlockResponse.NotFound(problem)
                : new Contract.StepsBlockResponse.BadRequest(problem));
        }

        return Task.FromResult<Contract.StepsBlockResponse>(new Contract.StepsBlockResponse.Ok(blocked));
    }
}

public sealed class UnblockStepHandler(StepBlockService blocks) : Contract.IStepsUnblockHandler
{
    public Task<Contract.StepsUnblockResponse> HandleAsync(Contract.StepsUnblockRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (StepViews.ChangeBlock(blocks, id, request.Step, block: false).TryPickProblems(out var problems, out var blocked))
        {
            var problem = ApiProblems.Of([.. problems]);
            return Task.FromResult<Contract.StepsUnblockResponse>(problem.Status == StatusCodes.Status404NotFound
                ? new Contract.StepsUnblockResponse.NotFound(problem)
                : new Contract.StepsUnblockResponse.BadRequest(problem));
        }

        return Task.FromResult<Contract.StepsUnblockResponse>(new Contract.StepsUnblockResponse.Ok(blocked));
    }
}

/// <summary>The step's attempts across runs, by identity (spec §6.3).</summary>
public sealed class GetStepHistoryHandler(PipelineService pipelines, RunViewService views) : Contract.IStepsHistoryHandler
{
    public Task<Contract.StepsHistoryResponse> HandleAsync(Contract.StepsHistoryRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Contract.StepsHistoryResponse>(new Contract.StepsHistoryResponse.NotFound(PipelineViews.NotFound(id)));
        var history = views.History(id, request.Step, Math.Clamp(request.Limit ?? 50, 1, 500));
        return Task.FromResult<Contract.StepsHistoryResponse>(new Contract.StepsHistoryResponse.Ok([.. history.Select(StepViews.ToApi)]));
    }
}

public static class StepViews
{
    public static Result<IReadOnlyList<string>> ChangeBlock(StepBlockService blocks, Id<Pipeline> pipelineId, string step, bool block)
    {
        if (!ManifestValidator.IsValidStepName(step))
            return ValidationProblem.ForField("step", "'{0}' is not a valid step name.", step);

        var changed = block ? blocks.Block(pipelineId, step) : blocks.Unblock(pipelineId, step);
        return changed.TryPickProblems(out var problems)
            ? Result.Failure<IReadOnlyList<string>>(problems)
            : Result.Success(blocks.ListBlocked(pipelineId));
    }

    public static Contract.StepHistoryItem ToApi(StepHistoryItem h) => new()
    {
        RunNumber = h.RunNumber,
        RunStatus = PipelineViews.ToApi(h.RunStatus),
        VersionNumber = h.VersionNumber,
        VersionStamp = h.VersionStamp,
        AttemptId = h.AttemptId.Value.Value,
        AttemptNumber = h.AttemptNumber,
        Status = h.Status,
        Reason = h.Reason,
        CreatedAt = h.CreatedAt,
        StartedAt = h.StartedAt,
        FinishedAt = h.FinishedAt,
        StepVersion = h.StepVersion,
        StepVersionChanged = h.StepVersionChanged,
        Phases = h.Phases is null ? null : ToApi(h.Phases),
    };

    public static Contract.AttemptPhases ToApi(AttemptPhases p) => new()
    {
        Queued = p.Queued,
        PodStart = p.PodStart,
        Fetch = p.Fetch,
        Image = p.Image,
        Active = p.Active,
        Collect = p.Collect,
        Notice = p.Notice,
    };
}
