using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Processing;

public class VerificationService
{
    private readonly EntityStore<Verification> _store;
    private readonly EntityStoreIndex<Verification, Id<ProcessingStep>> _byProcessingStep;

    public VerificationService(EntityStore<Verification> store)
    {
        _store = store;
        _byProcessingStep = store.CreateIndex(v => v.ProcessingStepId);
    }

    public void Set(Verification verification) => _store.Set(verification);

    public bool TryGet(Id<Verification> id, [NotNullWhen(true)] out Verification? verification)
        => _store.TryGet(id, out verification);

    public IReadOnlyList<Verification> GetByProcessingStepId(Id<ProcessingStep> processingStepId)
    {
        var ids = _byProcessingStep.GetForKey(processingStepId);
        var results = new List<Verification>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var verification))
                results.Add(verification);
        }
        return results;
    }

    public bool Delete(Id<Verification> id) => _store.Delete(id);
}
