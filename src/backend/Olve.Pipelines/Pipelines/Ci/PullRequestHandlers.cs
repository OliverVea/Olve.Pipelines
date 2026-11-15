using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>Open pull requests newest first (by number); closed and merged ones by when GitHub last changed them, newest first.</summary>
[OperationHandler]
public sealed class ListPullsHandler(PipelineService pipelines, PullRequestService pulls) : Contract.ICiPullsListHandler
{
    public Task<Contract.CiPullsListResponse> HandleAsync(Contract.CiPullsListRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Contract.CiPullsListResponse>(new Contract.CiPullsListResponse.NotFound(PipelineViews.NotFound(id)));

        var all = pulls.ListByPipeline(id);
        IReadOnlyList<PullRequest> listed = request.State == "closed"
            ? [.. all.Where(p => p.State != PullRequestState.Open).OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Number)]
            : [.. all.Where(p => p.State == PullRequestState.Open).OrderByDescending(p => p.Number)];
        var offset = Math.Max(0, request.Offset ?? 0);
        var limit = Math.Clamp(request.Limit ?? 50, 1, 500);
        return Task.FromResult<Contract.CiPullsListResponse>(new Contract.PullList
        {
            Items = [.. listed.Skip(offset).Take(limit).Select(PullViews.ToApi)],
            Offset = offset,
            Limit = limit,
            Total = listed.Count,
        });
    }
}

[OperationHandler]
public sealed class GetPullHandler(PipelineService pipelines, PullRequestService pulls) : Contract.ICiPullsGetHandler
{
    public Task<Contract.CiPullsGetResponse> HandleAsync(Contract.CiPullsGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Contract.CiPullsGetResponse>(new Contract.CiPullsGetResponse.NotFound(PipelineViews.NotFound(id)));
        if (!pulls.TryGet(id, request.Pull, out var pull))
            return Task.FromResult<Contract.CiPullsGetResponse>(new Contract.CiPullsGetResponse.NotFound(
                ApiProblems.Of(new NotFoundProblem("Pull request #{0} not found in pipeline '{1}'.", request.Pull, id))));

        var view = PullViews.ToApi(pull);
        return Task.FromResult<Contract.CiPullsGetResponse>(new Contract.PullDetail
        {
            Number = view.Number,
            Title = view.Title,
            Branch = view.Branch,
            HeadSha = view.HeadSha,
            Draft = view.Draft,
            State = view.State,
            Conflict = view.Conflict,
            Stale = view.Stale,
            LatestRun = view.LatestRun,
            Queue = view.Queue,
            UpdatedAt = view.UpdatedAt,
            Runs = [],
        });
    }
}

public static class PullViews
{
    public static Contract.Pull ToApi(PullRequest pull) => new()
    {
        Number = pull.Number,
        Title = pull.Title,
        Branch = pull.Branch,
        HeadSha = pull.HeadSha,
        Draft = pull.Draft,
        State = pull.State switch
        {
            PullRequestState.Merged => Contract.PullState.Merged,
            PullRequestState.Closed => Contract.PullState.Closed,
            _ => Contract.PullState.Open,
        },
        Conflict = pull.Mergeable == false,
        Stale = false,
        LatestRun = null,
        Queue = null,
        UpdatedAt = pull.UpdatedAt,
    };
}
