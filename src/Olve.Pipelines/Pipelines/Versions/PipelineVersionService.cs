using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Versions;

/// <summary>
/// CRUD and lookups over <see cref="PipelineVersion"/>. Singleton: it owns an index (see #26) and
/// the creation lock that keeps version numbers unique per pipeline.
/// </summary>
public class PipelineVersionService(
    EntityStore<PipelineVersion> store,
    EntityStore<Pipeline> pipelines,
    PipelineNumberingService numbering,
    IdProvider idProvider,
    TimeProvider timeProvider)
{
    private readonly EntityStoreIndex<PipelineVersion, Id<Pipeline>> _byPipeline = store.CreateIndex(v => v.PipelineId);
    private readonly Lock _createGate = new();

    /// <summary>
    /// Adds the next version of a pipeline. The hash and entity version maps are computed by the
    /// manifest layer (B2); this only validates shape and allocates the number. <paramref name="binaryKey"/> is
    /// where reconcile's build Job stored the compiled harness (default: <see cref="StorageLayout.VersionBinaryKey"/>).
    /// Never deduplicates — callers compare <see cref="PipelineVersion.ManifestHash"/> with
    /// <see cref="TryGetLatest"/> first.
    /// </summary>
    public Result<PipelineVersion> Add(
        Id<Pipeline> pipelineId,
        string manifest,
        string manifestHash,
        string? commitSha,
        IReadOnlyDictionary<string, int> stepVersions,
        IReadOnlyDictionary<string, int> modelVersions,
        string? binaryKey = null)
    {
        if (!pipelines.Contains(pipelineId))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (string.IsNullOrWhiteSpace(manifestHash))
            return new ResultProblem("Manifest hash is required.");
        if (!JsonText.IsWellFormed(manifest))
            return new ResultProblem("Manifest must be well-formed JSON.");

        lock (_createGate)
        {
            var number = numbering.AllocateVersionNumber(pipelineId, MaxNumber(pipelineId));
            var version = new PipelineVersion(
                idProvider.Create<PipelineVersion>(),
                pipelineId,
                number,
                manifest,
                manifestHash,
                commitSha,
                binaryKey ?? StorageLayout.VersionBinaryKey(pipelineId, number),
                new Dictionary<string, int>(stepVersions, StringComparer.Ordinal),
                new Dictionary<string, int>(modelVersions, StringComparer.Ordinal),
                timeProvider.GetUtcNow());
            store.Set(version);
            return version;
        }
    }

    public bool TryGet(Id<PipelineVersion> id, [NotNullWhen(true)] out PipelineVersion? version)
        => store.TryGet(id, out version);

    /// <summary>All versions of a pipeline, newest first.</summary>
    public IReadOnlyList<PipelineVersion> ListByPipeline(Id<Pipeline> pipelineId)
        => Resolve(pipelineId).OrderByDescending(v => v.Number).ToList();

    public bool TryGetByNumber(Id<Pipeline> pipelineId, int number, [NotNullWhen(true)] out PipelineVersion? version)
    {
        version = Resolve(pipelineId).FirstOrDefault(v => v.Number == number);
        return version is not null;
    }

    public bool TryGetLatest(Id<Pipeline> pipelineId, [NotNullWhen(true)] out PipelineVersion? version)
    {
        version = Resolve(pipelineId).MaxBy(v => v.Number);
        return version is not null;
    }

    /// <summary>The number the next <see cref="Add"/> for this pipeline would allocate.</summary>
    public int PeekNextNumber(Id<Pipeline> pipelineId)
        => numbering.PeekNextVersionNumber(pipelineId, MaxNumber(pipelineId));

    public DeletionResult Delete(Id<PipelineVersion> id) => store.Delete(id);

    private int MaxNumber(Id<Pipeline> pipelineId)
        => Resolve(pipelineId).Select(v => v.Number).DefaultIfEmpty(0).Max();

    private IEnumerable<PipelineVersion> Resolve(Id<Pipeline> pipelineId)
    {
        foreach (var id in _byPipeline.GetForKey(pipelineId))
        {
            if (store.TryGet(id, out var version))
                yield return version;
        }
    }
}
