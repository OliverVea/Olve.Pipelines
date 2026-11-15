using System.Text.Json.Serialization;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// A pull request into a binding's branch, from the same repository, as GitHub last reported it. Tracked whether or
/// not the pipeline declares CI, so a pull request that adds CI is seen.
/// </summary>
/// <param name="Branch">The head branch.</param>
/// <param name="Mergeable">GitHub can merge it onto the base; null while GitHub hasn't computed it.</param>
/// <param name="UpdatedAt">GitHub's <c>updated_at</c>: an older report than the one held is ignored.</param>
/// <param name="LastPushAt">When the service first saw <see cref="HeadSha"/>.</param>
/// <param name="AutoMerge">GitHub auto-merge is turned on: the pull request asks for the merge queue.</param>
public sealed record PullRequest(
    Id<PullRequest> Id,
    Id<Pipeline> PipelineId,
    int Number,
    string Title,
    string Branch,
    string HeadSha,
    bool Draft,
    PullRequestState State,
    bool? Mergeable,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastPushAt,
    bool AutoMerge = false) : IHasId<Id<PullRequest>>;

[JsonConverter(typeof(JsonStringEnumConverter<PullRequestState>))]
public enum PullRequestState
{
    Open,
    Merged,
    Closed,
}

public class PullRequestEvents
{
    public Event<EntityAdded<PullRequest, Id<PullRequest>>> OnAdded { get; } = new();
    public Event<EntityUpdated<PullRequest, Id<PullRequest>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<PullRequest, Id<PullRequest>>> OnDeleted { get; } = new();
}
