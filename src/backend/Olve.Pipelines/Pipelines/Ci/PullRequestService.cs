using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>What a report of a pull request did to the tracked ones.</summary>
public enum PullRequestChange
{
    /// <summary>Added or updated (also when nothing differed).</summary>
    Tracked,

    /// <summary>It no longer targets the binding's branch, so it is not tracked any more.</summary>
    Removed,

    /// <summary>From a fork, into another branch, or older than what is held.</summary>
    Ignored,
}

/// <summary>The pull requests of each pipeline's binding. Singleton: owns the indexes.</summary>
public class PullRequestService(EntityStore<PullRequest> store, IdProvider idProvider)
{
    private readonly EntityStoreIndex<PullRequest, Id<Pipeline>> _byPipeline = store.CreateIndex(p => p.PipelineId);
    private readonly EntityStoreUniqueIndex<PullRequest, Id<PullRequest>, (Id<Pipeline>, int)> _byNumber =
        store.CreateUniqueIndex<PullRequest, Id<PullRequest>, (Id<Pipeline>, int)>(p => (p.PipelineId, p.Number));
    private readonly Lock _gate = new();

    public bool TryGet(Id<Pipeline> pipelineId, int number, [NotNullWhen(true)] out PullRequest? pull)
    {
        pull = null;
        return _byNumber.TryGet((pipelineId, number), out var id) && store.TryGet(id, out pull);
    }

    public IReadOnlyList<PullRequest> ListByPipeline(Id<Pipeline> pipelineId) =>
        [.. _byPipeline.GetForKey(pipelineId).Select(id => store.TryGet(id, out var p) ? p : null).OfType<PullRequest>()];

    /// <summary>Applies GitHub's view of a pull request of the repository bound to <paramref name="branch"/>.</summary>
    public PullRequestChange Observe(Id<Pipeline> pipelineId, string branch, GitHubPullRequest reported, DateTimeOffset seenAt)
    {
        lock (_gate)
        {
            TryGet(pipelineId, reported.Number, out var held);
            if (reported.FromFork)
                return PullRequestChange.Ignored;
            if (held is not null && reported.UpdatedAt < held.UpdatedAt)
                return PullRequestChange.Ignored;
            if (!string.Equals(reported.Base.Ref, branch, StringComparison.Ordinal))
            {
                if (held is null)
                    return PullRequestChange.Ignored;
                store.Delete(held.Id).DiscardResult();
                return PullRequestChange.Removed;
            }

            var samePush = held?.HeadSha == reported.Head.Sha;
            var pull = new PullRequest(
                held?.Id ?? idProvider.Create<PullRequest>(),
                pipelineId,
                reported.Number,
                reported.Title,
                reported.Head.Ref,
                reported.Head.Sha,
                reported.Draft,
                reported.MergedAt is not null ? PullRequestState.Merged
                    : reported.State == "closed" ? PullRequestState.Closed
                    : PullRequestState.Open,
                reported.Mergeable ?? (samePush ? held!.Mergeable : null),
                reported.UpdatedAt,
                samePush ? held!.LastPushAt : seenAt,
                reported.AutoMerge is not null);
            if (pull != held)
                store.Set(pull);
            return PullRequestChange.Tracked;
        }
    }

    /// <summary>Stops tracking a pull request GitHub no longer has.</summary>
    public void Forget(Id<Pipeline> pipelineId, int number)
    {
        lock (_gate)
        {
            if (TryGet(pipelineId, number, out var held))
                store.Delete(held.Id).DiscardResult();
        }
    }
}
