using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Processing;

public class ProcessingStepService
{
    private readonly EntityStore<ProcessingStep> _store;
    private readonly EntityStoreIndex<ProcessingStep, Id<Pipeline>> _byPipeline;
    private readonly AttachmentStore<ProcessingStep, ScriptProcessing> _script;

    public ProcessingStepService(
        EntityStore<ProcessingStep> store,
        AttachmentStore<ProcessingStep, ScriptProcessing> script)
    {
        _store = store;
        _byPipeline = store.CreateIndex(p => p.PipelineId);
        _script = script;
    }

    public void Set(ProcessingStep step) => _store.Set(step);

    public bool TryGet(Id<ProcessingStep> id, [NotNullWhen(true)] out ProcessingStep? step)
        => _store.TryGet(id, out step);

    public IReadOnlyList<ProcessingStep> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<ProcessingStep>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var step))
                results.Add(step);
        }
        return results;
    }

    public DeletionResult Delete(Id<ProcessingStep> id) => _store.Delete(id);

    public void SetScript(Id<ProcessingStep> id, ScriptProcessing script)
    {
        _script.Set(id, script);
        UpdateType(id, ProcessingStepType.Script);
    }

    public bool TryGetScript(Id<ProcessingStep> id, [NotNullWhen(true)] out ScriptProcessing? script)
        => _script.TryGet(id, out script);

    public bool RemoveScript(Id<ProcessingStep> id)
    {
        if (!_script.Remove(id)) return false;
        UpdateType(id, ProcessingStepType.None);
        return true;
    }

    private void UpdateType(Id<ProcessingStep> id, ProcessingStepType type)
    {
        if (_store.TryGet(id, out var entity))
            _store.Set(entity with { Type = type });
    }
}
