using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.FailureHandlers;

/// <summary>
/// Reads and writes a pipeline's failure-handler bindings (see <see cref="FailureHandlerBindings"/>),
/// kept in an <see cref="AttachmentStore{Pipeline,T}"/> keyed on the pipeline Id so they are
/// auto-cleaned when the pipeline is deleted. Absence means no handlers. Reconcile is the writer;
/// <see cref="FailureHandlerService"/> is the reader.
/// </summary>
public class FailureHandlerBindingService(AttachmentStore<Pipeline, FailureHandlerBindings> store)
{
    /// <summary>The bindings attached to the pipeline, or an empty list when none are attached.</summary>
    public IReadOnlyList<FailureHandlerBinding> Get(Id<Pipeline> pipelineId)
        => store.TryGet(pipelineId, out var bindings) ? bindings.Bindings : [];

    /// <summary>Replaces the pipeline's bindings, or clears them when the list is empty.</summary>
    public void Set(Id<Pipeline> pipelineId, IReadOnlyList<FailureHandlerBinding> bindings)
    {
        if (bindings.Count == 0)
            store.Remove(pipelineId);
        else
            store.Set(pipelineId, new FailureHandlerBindings(bindings));
    }
}
