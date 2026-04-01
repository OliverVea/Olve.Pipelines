using System.Diagnostics.CodeAnalysis;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineBuilders;

public class PipelineBuilderService
{
    private readonly EntityStore<PipelineBuilder> _store;
    private readonly EntityStoreIndex<PipelineBuilder, Id<Pipeline>> _byPipeline;
    private readonly AttachmentStore<PipelineBuilder, ScriptBuilder> _script;

    public PipelineBuilderService(
        EntityStore<PipelineBuilder> store,
        AttachmentStore<PipelineBuilder, ScriptBuilder> script)
    {
        _store = store;
        _byPipeline = store.CreateIndex(b => b.PipelineId);
        _script = script;
    }

    public void Set(PipelineBuilder builder) => _store.Set(builder);

    public bool TryGet(Id<PipelineBuilder> id, [NotNullWhen(true)] out PipelineBuilder? builder)
        => _store.TryGet(id, out builder);

    public IReadOnlyList<PipelineBuilder> GetByPipelineId(Id<Pipeline> pipelineId)
    {
        var ids = _byPipeline.GetForKey(pipelineId);
        var results = new List<PipelineBuilder>(ids.Count);
        foreach (var id in ids)
        {
            if (_store.TryGet(id, out var builder))
                results.Add(builder);
        }
        return results;
    }

    public DeletionResult Delete(Id<PipelineBuilder> id) => _store.Delete(id);

    public void SetScript(Id<PipelineBuilder> id, ScriptBuilder script)
    {
        _script.Set(id, script);
        UpdateType(id, PipelineBuilderType.Script);
    }

    public bool TryGetScript(Id<PipelineBuilder> id, [NotNullWhen(true)] out ScriptBuilder? script)
        => _script.TryGet(id, out script);

    public bool RemoveScript(Id<PipelineBuilder> id)
    {
        if (!_script.Remove(id)) return false;
        UpdateType(id, PipelineBuilderType.None);
        return true;
    }

    private void UpdateType(Id<PipelineBuilder> id, PipelineBuilderType type)
    {
        if (_store.TryGet(id, out var entity))
            _store.Set(entity with { Type = type });
    }
}
