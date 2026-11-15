using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Olve.Pipelines.Jobs;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// A pull request's preview: what its CI runs deployed, removed by a close run once it closes or merges. Recorded when
/// a never-reused step of a pull request's run is scheduled, before anything is deployed, so a deploy that fails or is
/// cancelled halfway is still removed. Only a pipeline whose CI declares <c>onClose</c> has previews.
/// </summary>
/// <param name="DeployedBy">The CI run that last deployed it; its definition and commit remove it.</param>
/// <param name="Links">Every link the never-reused steps of <see cref="DeployedBy"/> gave, in step order (null = none yet).</param>
/// <param name="CloseRun">The close run removing it, the latest one once it is removed or failing.</param>
/// <param name="Failures">Close runs that failed in a row; the next one is due at <see cref="RetryAt"/>.</param>
public sealed record Preview(
    Id<Preview> Id,
    Id<Pipeline> PipelineId,
    int Pull,
    PreviewState State,
    Id<CiRun> DeployedBy,
    int DeployedByNumber,
    DateTimeOffset DeployedAt,
    IReadOnlyList<Runs.AttemptLink>? Links = null,
    Id<CiRun>? CloseRun = null,
    int Failures = 0,
    DateTimeOffset? RetryAt = null) : IHasId<Id<Preview>>;

[JsonConverter(typeof(JsonStringEnumConverter<PreviewState>))]
public enum PreviewState
{
    Deploying,
    Live,
    Removing,
    Removed,
    Failing,
}

public class PreviewEvents
{
    public Event<EntityAdded<Preview, Id<Preview>>> OnAdded { get; } = new();
    public Event<EntityUpdated<Preview, Id<Preview>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<Preview, Id<Preview>>> OnDeleted { get; } = new();
}

/// <summary>Previews, one per (pipeline, pull request). Singleton: owns the index.</summary>
public class PreviewService(EntityStore<Preview> store, IdProvider idProvider)
{
    private readonly EntityStoreUniqueIndex<Preview, Id<Preview>, (Id<Pipeline>, int)> _byPull =
        store.CreateUniqueIndex<Preview, Id<Preview>, (Id<Pipeline>, int)>(p => (p.PipelineId, p.Pull));

    public bool TryGet(Id<Pipeline> pipelineId, int pull, [NotNullWhen(true)] out Preview? preview)
    {
        preview = null;
        return _byPull.TryGet((pipelineId, pull), out var id) && store.TryGet(id, out preview);
    }

    public IReadOnlyList<Preview> List() => store.List();

    /// <summary>The run is deploying the pull request's preview (again).</summary>
    public Preview Deploying(CiRun run, DateTimeOffset now)
    {
        if (TryGet(run.PipelineId, run.Pull, out var existing))
        {
            if (existing.DeployedBy == run.Id && existing.State is PreviewState.Deploying)
                return existing;
            var deploying = existing with
            {
                State = PreviewState.Deploying,
                DeployedBy = run.Id,
                DeployedByNumber = run.Number,
                DeployedAt = now,
                Links = existing.DeployedBy == run.Id ? existing.Links : null,
                Failures = 0,
                RetryAt = null,
            };
            store.Set(deploying);
            return deploying;
        }

        var created = new Preview(idProvider.Create<Preview>(), run.PipelineId, run.Pull, PreviewState.Deploying, run.Id, run.Number, now);
        store.Set(created);
        return created;
    }

    public Preview Update(Preview preview, Func<Preview, Preview> change)
    {
        if (!store.TryGet(preview.Id, out var current))
            return preview;
        var changed = change(current);
        if (changed != current)
            store.Set(changed);
        return changed;
    }

    public void Delete(Preview preview) => store.Delete(preview.Id).DiscardResult();
}
