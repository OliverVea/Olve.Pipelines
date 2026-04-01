using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Processing;

public class VerificationService
{
    private readonly EntityStore<Verification> _store;
    private readonly EntityStoreIndex<Verification, Id<ProcessingStep>> _byProcessingStep;
    private readonly AttachmentStore<Verification, ScriptVerification> _script;

    public VerificationService(
        EntityStore<Verification> store,
        AttachmentStore<Verification, ScriptVerification> script)
    {
        _store = store;
        _byProcessingStep = store.CreateIndex(v => v.ProcessingStepId);
        _script = script;
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

    public DeletionResult Delete(Id<Verification> id) => _store.Delete(id);

    public void SetScript(Id<Verification> id, ScriptVerification script)
    {
        _script.Set(id, script);
        UpdateType(id, VerificationType.Script);
    }

    public bool TryGetScript(Id<Verification> id, [NotNullWhen(true)] out ScriptVerification? script)
        => _script.TryGet(id, out script);

    public bool RemoveScript(Id<Verification> id)
    {
        if (!_script.Remove(id)) return false;
        UpdateType(id, VerificationType.None);
        return true;
    }

    private void UpdateType(Id<Verification> id, VerificationType type)
    {
        if (_store.TryGet(id, out var entity))
            _store.Set(entity with { Type = type });
    }
}
