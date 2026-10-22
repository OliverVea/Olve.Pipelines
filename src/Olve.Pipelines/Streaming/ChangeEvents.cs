using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Streaming;

/// <summary>A "something changed" notification on <c>GET /api/events</c>: the client re-fetches the affected view.</summary>
/// <param name="Type">The SSE event name.</param>
/// <param name="Data">The payload, serialized once for every client.</param>
public sealed record ChangeEvent(string Type, Id<Pipeline> PipelineId, string Data)
{
    public static ChangeEvent Run(Run run, bool deleted = false)
        => Of("run", run.PipelineId, new RunChanged(run.PipelineId, run.Number, deleted ? "Deleted" : run.Status.ToString()),
            StreamingJsonContext.Default.RunChanged);

    public static ChangeEvent Attempt(Attempt attempt)
        => Of("attempt", attempt.PipelineId,
            new AttemptChanged(attempt.PipelineId, attempt.RunNumber, attempt.StepName, attempt.Number, attempt.Status.Discriminator()),
            StreamingJsonContext.Default.AttemptChanged);

    public static ChangeEvent VersionAdded(PipelineVersion version)
        => Of("version", version.PipelineId, new VersionChanged(version.PipelineId, version.Number, "added"),
            StreamingJsonContext.Default.VersionChanged);

    public static ChangeEvent Activation(Id<Pipeline> pipelineId, PipelineActivation? activation)
        => Of("version", pipelineId, activation switch
        {
            PipelineActivation.Active a => new VersionChanged(pipelineId, a.VersionNumber, "active"),
            PipelineActivation.Draining d => new VersionChanged(pipelineId, d.ToVersionNumber, "draining"),
            _ => new VersionChanged(pipelineId, null, "none"),
        }, StreamingJsonContext.Default.VersionChanged);

    public static ChangeEvent Gate(Id<Pipeline> pipelineId, IReadOnlyList<string> blockedSteps)
        => Of("gate", pipelineId, new GateChanged(pipelineId, blockedSteps), StreamingJsonContext.Default.GateChanged);

    public static ChangeEvent Binding(PipelineConfigBinding binding, bool deleted = false)
        => Of("binding", binding.PipelineId,
            new BindingChanged(binding.PipelineId, deleted ? "removed" : binding.Status.InProgress ? "reconciling" : binding.Status.Result.ToString()),
            StreamingJsonContext.Default.BindingChanged);

    public static ChangeEvent Pipeline(Id<Pipeline> pipelineId, string change)
        => Of("pipeline", pipelineId, new PipelineChanged(pipelineId, change), StreamingJsonContext.Default.PipelineChanged);

    private static ChangeEvent Of<T>(string type, Id<Pipeline> pipelineId, T payload, JsonTypeInfo<T> typeInfo)
        => new(type, pipelineId, JsonSerializer.Serialize(payload, typeInfo));
}

/// <param name="Status">Running / Succeeded / Failed / Cancelled / Superseded, or Deleted.</param>
public record RunChanged(Id<Pipeline> PipelineId, int Run, string Status);

/// <param name="Status">scheduled / in-progress / succeeded / failed / skipped / cancelled / obsolete.</param>
public record AttemptChanged(Id<Pipeline> PipelineId, int Run, string Step, int Attempt, string Status);

/// <param name="Change">added (a new version), active (switched to / switch done) or draining (a switch to <paramref name="Version"/> started).</param>
public record VersionChanged(Id<Pipeline> PipelineId, int? Version, string Change);

public record GateChanged(Id<Pipeline> PipelineId, IReadOnlyList<string> BlockedSteps);

/// <param name="State">reconciling, Success, Error, NeverRun, or removed.</param>
public record BindingChanged(Id<Pipeline> PipelineId, string State);

/// <param name="Change">added, updated or removed.</param>
public record PipelineChanged(Id<Pipeline> PipelineId, string Change);

/// <summary>A chunk of a live log stream; <paramref name="Reset"/> replaces everything shown so far.</summary>
public record LogChunk(string Text, bool Reset = false);

public record LogStreamEnd(string Status);

public record StreamHello(string? LastEventId);

public record StreamResync(string Reason);
