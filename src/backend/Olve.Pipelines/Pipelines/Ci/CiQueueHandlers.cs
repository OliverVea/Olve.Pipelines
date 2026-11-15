using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

[OperationHandler]
public sealed class GetQueueHandler(PipelineService pipelines, CiRunViewService views) : Contract.ICiQueueGetHandler
{
    public Task<Contract.CiQueueGetResponse> HandleAsync(Contract.CiQueueGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.CiQueueGetResponse>(pipelines.TryGet(id, out _)
            ? views.Queue(id)
            : new Contract.CiQueueGetResponse.NotFound(PipelineViews.NotFound(id)));
    }
}

/// <summary>
/// Enqueues an open pull request into the binding's branch. One the service doesn't track is read from GitHub first,
/// so a pull request from a fork, into another branch or by an author without CI is refused by name. Replies once the entry is saved.
/// </summary>
[OperationHandler]
public sealed class EnqueuePullHandler(
    PipelineService pipelines,
    PipelineConfigBindingService bindings,
    PullRequestService pulls,
    IPullRequestSource source,
    CiOptions ci,
    CiQueueActions actions,
    CiRunViewService views,
    CiPersistenceService durability,
    IHttpContextAccessor http,
    TimeProvider time) : Contract.ICiQueueEnqueueHandler
{
    public async Task<Contract.CiQueueEnqueueResponse> HandleAsync(Contract.CiQueueEnqueueRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return new Contract.CiQueueEnqueueResponse.NotFound(PipelineViews.NotFound(id));

        var number = request.Body.Pull;
        if (!pulls.TryGet(id, number, out _) && !bindings.GetByPipelineId(id).TryPickProblems(out _, out var binding))
        {
            if ((await source.GetAsync(binding, number, cancellationToken)).TryPickProblems(out var readProblems, out var reported))
                return Fail(readProblems);
            if (reported is not null)
            {
                if (reported.FromFork)
                    return Fail(new ConflictProblem("Pull request #{0} comes from a fork.", number));
                if (reported.Base.Ref != binding.Branch)
                    return Fail(new ConflictProblem("Pull request #{0} is not into '{1}'.", number, binding.Branch));
                if (!ci.Allows(reported.User?.Login))
                    return Fail(new ConflictProblem("Pull request #{0} is by '{1}', whose pull requests get no CI.", number, reported.User?.Login ?? "ghost"));
                pulls.Observe(id, binding.Branch, reported, time.GetUtcNow());
            }
        }

        if (actions.Enqueue(id, number, CiQueueVia.Api, SecretViews.Actor(http.HttpContext?.User)).TryPickProblems(out var problems, out var entry)
            || (await durability.SavedAsync(cancellationToken)).TryPickProblems(out problems))
            return Fail(problems);
        return views.Entry(entry);
    }

    private static Contract.CiQueueEnqueueResponse Fail(ResultProblemCollection problems) => RunViews.Fail<Contract.CiQueueEnqueueResponse>(problems,
        p => new Contract.CiQueueEnqueueResponse.NotFound(p), p => new Contract.CiQueueEnqueueResponse.Conflict(p),
        p => new Contract.CiQueueEnqueueResponse.BadRequest(p), p => new Contract.CiQueueEnqueueResponse.ServiceUnavailable(p));

    private static Contract.CiQueueEnqueueResponse Fail(ConflictProblem problem) => new Contract.CiQueueEnqueueResponse.Conflict(ApiProblems.Of(problem));
}

/// <summary>Takes a pull request out of the queue; a queue run testing it is cancelled. Replies once that is saved.</summary>
[OperationHandler]
public sealed class DequeuePullHandler(CiQueueActions actions, CiRunViewService views, CiPersistenceService durability, IHttpContextAccessor http)
    : Contract.ICiQueueDequeueHandler
{
    public async Task<Contract.CiQueueDequeueResponse> HandleAsync(Contract.CiQueueDequeueRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (actions.Dequeue(id, request.Pull, SecretViews.Actor(http.HttpContext?.User)).TryPickProblems(out var problems, out var entry)
            || (await durability.SavedAsync(cancellationToken)).TryPickProblems(out problems))
            return RunViews.Fail<Contract.CiQueueDequeueResponse>(problems,
                p => new Contract.CiQueueDequeueResponse.NotFound(p), p => new Contract.CiQueueDequeueResponse.Conflict(p),
                p => new Contract.CiQueueDequeueResponse.Conflict(p), p => new Contract.CiQueueDequeueResponse.ServiceUnavailable(p));
        return views.Entry(entry);
    }
}
