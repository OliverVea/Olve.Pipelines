using System.Collections.Concurrent;

namespace Olve.Pipelines.Pipelines.Processing;

/// <summary>
/// One lock per pipeline, held around "read the gate (and the job history), then create a processing
/// run" by every path that starts one: the automatic cascade (<c>DownstreamTriggerService</c>),
/// Promote, block/unblock, the manual <c>trigger/processing</c> endpoint and processing triggers. Serialising them means an unblock
/// and a cascade arriving together cannot both start the same step, and a block cannot slip between a
/// promote's gate check and its job creation. Singleton: it is state.
/// </summary>
public class PromotionLocks
{
    private readonly ConcurrentDictionary<Id<Pipeline>, object> _locks = new();

    public object For(Id<Pipeline> pipelineId) => _locks.GetOrAdd(pipelineId, _ => new object());
}
