using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Per-pipeline high-water marks for the monotonic run and version numbers. Persisted so a number is
/// never handed out twice — not even after the newest run/version was deleted — because run numbers
/// are addresses (<c>#412</c>, version stamps, <c>runs/&lt;p&gt;/412/…</c> keys) and versions name
/// binaries in storage. Attached to the pipeline (operational state), removed with it.
/// </summary>
public record PipelineSequences(int LastRunNumber = 0, int LastVersionNumber = 0);

/// <summary>
/// Allocates run/version numbers. Singleton: the lock serialises every allocation so two concurrent
/// creates can never get the same number. Each allocation takes a <c>floor</c> — the highest number
/// currently in the store — so a lost or stale sequence snapshot can never cause reuse either.
/// </summary>
public class PipelineNumberingService(AttachmentStore<Pipeline, PipelineSequences> sequences)
{
    private readonly Lock _gate = new();

    public int AllocateRunNumber(Id<Pipeline> pipelineId, int floor)
    {
        lock (_gate)
        {
            var current = Get(pipelineId);
            var next = Math.Max(current.LastRunNumber, floor) + 1;
            sequences.Set(pipelineId, current with { LastRunNumber = next });
            return next;
        }
    }

    public int AllocateVersionNumber(Id<Pipeline> pipelineId, int floor)
    {
        lock (_gate)
        {
            var current = Get(pipelineId);
            var next = Math.Max(current.LastVersionNumber, floor) + 1;
            sequences.Set(pipelineId, current with { LastVersionNumber = next });
            return next;
        }
    }

    public int PeekNextRunNumber(Id<Pipeline> pipelineId, int floor)
        => Math.Max(Get(pipelineId).LastRunNumber, floor) + 1;

    public int PeekNextVersionNumber(Id<Pipeline> pipelineId, int floor)
        => Math.Max(Get(pipelineId).LastVersionNumber, floor) + 1;

    private PipelineSequences Get(Id<Pipeline> pipelineId)
        => sequences.TryGet(pipelineId, out var s) ? s : new PipelineSequences();
}
