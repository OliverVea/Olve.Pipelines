using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>Open pull requests newest first (by number); closed and merged ones by when GitHub last changed them, newest first.</summary>
[OperationHandler]
public sealed class ListPullsHandler(PipelineService pipelines, PullRequestService pulls, CiRunViewService views) : Contract.ICiPullsListHandler
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
            Items = [.. listed.Skip(offset).Take(limit).Select(views.Pull)],
            Offset = offset,
            Limit = limit,
            Total = listed.Count,
        });
    }
}

[OperationHandler]
public sealed class GetPullHandler(PipelineService pipelines, PullRequestService pulls, CiRunViewService views) : Contract.ICiPullsGetHandler
{
    public Task<Contract.CiPullsGetResponse> HandleAsync(Contract.CiPullsGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Contract.CiPullsGetResponse>(new Contract.CiPullsGetResponse.NotFound(PipelineViews.NotFound(id)));
        if (!pulls.TryGet(id, request.Pull, out var pull))
            return Task.FromResult<Contract.CiPullsGetResponse>(new Contract.CiPullsGetResponse.NotFound(
                ApiProblems.Of(new NotFoundProblem("Pull request #{0} not found in pipeline '{1}'.", request.Pull, id))));

        return Task.FromResult<Contract.CiPullsGetResponse>(views.PullDetail(pull));
    }
}
