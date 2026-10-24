using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Olve.Pipelines.Jobs;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Configs;

/// <summary>
/// One saved, immutable, complete config document of a pipeline: <c>config #Number</c>. The YAML is kept
/// exactly as written (comments included); defaults are applied when it is read, never written into it.
/// </summary>
/// <param name="RestoredFrom">For a rollback: the snapshot whose content this one repeats.</param>
public record ConfigSnapshot(
    Id<ConfigSnapshot> Id,
    Id<Pipeline> PipelineId,
    int Number,
    string Yaml,
    string Author,
    DateTimeOffset CreatedAt,
    int? RestoredFrom = null) : IHasId<Id<ConfigSnapshot>>;

/// <summary>
/// Adds and reads config snapshots. Snapshots are never edited; they go only with their pipeline.
/// Singleton: owns the per-pipeline index, the numbering lock and the parsed-document cache.
/// </summary>
public class ConfigSnapshotService(
    EntityStore<ConfigSnapshot> store,
    EntityStore<Pipeline> pipelines,
    IdProvider idProvider,
    TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<ConfigSnapshot, Id<Pipeline>> _byPipeline = store.CreateIndex(s => s.PipelineId);
    private readonly ConcurrentDictionary<Id<ConfigSnapshot>, JsonObject> _documents = new();
    private readonly Lock _createGate = new();

    /// <summary>Saves <paramref name="yaml"/> as the pipeline's next snapshot. The YAML must parse; validation is the caller's.</summary>
    public Result<ConfigSnapshot> Add(Id<Pipeline> pipelineId, string yaml, string author, int? restoredFrom = null)
    {
        if (!pipelines.Contains(pipelineId))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (YamlJson.Parse(yaml).TryPickProblems(out var problems))
            return Result.Failure<ConfigSnapshot>(problems);

        lock (_createGate)
        {
            var number = Resolve(pipelineId).Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
            var snapshot = new ConfigSnapshot(idProvider.Create<ConfigSnapshot>(), pipelineId, number, yaml, author,
                timeProvider.GetUtcNow(), restoredFrom);
            store.Set(snapshot);
            return snapshot;
        }
    }

    public bool TryGetLatest(Id<Pipeline> pipelineId, [NotNullWhen(true)] out ConfigSnapshot? snapshot)
    {
        snapshot = Resolve(pipelineId).MaxBy(s => s.Number);
        return snapshot is not null;
    }

    public bool TryGetByNumber(Id<Pipeline> pipelineId, int number, [NotNullWhen(true)] out ConfigSnapshot? snapshot)
    {
        snapshot = Resolve(pipelineId).FirstOrDefault(s => s.Number == number);
        return snapshot is not null;
    }

    /// <summary>All snapshots of a pipeline, newest first.</summary>
    public IReadOnlyList<ConfigSnapshot> ListByPipeline(Id<Pipeline> pipelineId)
        => Resolve(pipelineId).OrderByDescending(s => s.Number).ToList();

    /// <summary>The snapshot's document as JSON (a fresh copy). Empty when there is no snapshot.</summary>
    public JsonObject Document(ConfigSnapshot? snapshot)
    {
        if (snapshot is null)
            return new JsonObject();
        var parsed = _documents.GetOrAdd(snapshot.Id, id => YamlJson.Parse(snapshot.Yaml).TryPickProblems(out _, out var doc) ? new JsonObject() : doc);
        return (JsonObject)parsed.DeepClone();
    }

    public void DeleteAll(Id<Pipeline> pipelineId)
    {
        foreach (var snapshot in Resolve(pipelineId).ToList())
        {
            store.Delete(snapshot.Id).DiscardResult();
            _documents.TryRemove(snapshot.Id, out _);
        }
    }

    private IEnumerable<ConfigSnapshot> Resolve(Id<Pipeline> pipelineId)
    {
        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (store.TryGet(id, out var snapshot))
                yield return snapshot;
        }
    }
}
