using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineProcessing;

public class PipelineVerificationService
{
    private readonly EntityStore<PipelineVerification> _store;
    private readonly EntityStoreIndex<PipelineVerification, Id<PipelineProcessingStep>> _byProcessing;

    public PipelineVerificationService(EntityStore<PipelineVerification> store)
    {
        _store = store;
        _byProcessing = store.CreateIndex(v => v.ProcessingId);
    }

    public void Set(PipelineVerification verification) => _store.Set(verification);

    public bool TryGet(Id<PipelineVerification> id, [NotNullWhen(true)] out PipelineVerification? verification)
        => _store.TryGet(id, out verification);

    public IReadOnlyList<PipelineVerification> GetByProcessingId(Id<PipelineProcessingStep> processingId)
    {
        var ids = _byProcessing.GetForKey(processingId);
        var results = new List<PipelineVerification>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var verification))
                results.Add(verification);
        }
        return results;
    }

    public bool Delete(Id<PipelineVerification> id) => _store.Delete(id);
}
