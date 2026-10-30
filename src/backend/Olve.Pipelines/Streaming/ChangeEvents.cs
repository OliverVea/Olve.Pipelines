using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Streaming;

/// <summary>A change to broadcast: the pipeline it concerns (the stream filter) and the notice clients get.</summary>
public sealed record ChangeEvent(Id<Pipeline> PipelineId, Contract.ChangeEvent Payload)
{
    public string Type => Payload.EventType;

    public static ChangeEvent Run(Run run, bool deleted = false)
        => new(run.PipelineId, new Contract.RunChanged
        {
            PipelineId = run.PipelineId.Value.Value,
            Run = run.Number,
            Status = deleted ? Contract.RunChangeStatus.Deleted : run.Status switch
            {
                RunStatus.Running => Contract.RunChangeStatus.Running,
                RunStatus.Succeeded => Contract.RunChangeStatus.Succeeded,
                RunStatus.Failed => Contract.RunChangeStatus.Failed,
                RunStatus.Cancelled => Contract.RunChangeStatus.Cancelled,
                _ => Contract.RunChangeStatus.Superseded,
            },
        });

    public static ChangeEvent Attempt(Attempt attempt)
        => new(attempt.PipelineId, new Contract.AttemptChanged
        {
            PipelineId = attempt.PipelineId.Value.Value,
            Run = attempt.RunNumber,
            Step = attempt.StepName,
            Attempt = attempt.Number,
            Status = attempt.Status.State(),
        });

    public static ChangeEvent VersionAdded(PipelineVersion version) => Version(version.PipelineId, version.Number, "added");

    public static ChangeEvent VersionDeleted(PipelineVersion version) => Version(version.PipelineId, version.Number, "deleted");

    public static ChangeEvent Activation(Id<Pipeline> pipelineId, PipelineActivation? activation) => activation switch
    {
        PipelineActivation.Active a => Version(pipelineId, a.VersionNumber, "active"),
        PipelineActivation.Draining d => Version(pipelineId, d.ToVersionNumber, "draining"),
        _ => Version(pipelineId, null, "none"),
    };

    public static ChangeEvent BlockedSteps(Id<Pipeline> pipelineId, IReadOnlyList<string> blockedSteps)
        => new(pipelineId, new Contract.BlockedStepsChanged { PipelineId = pipelineId.Value.Value, BlockedSteps = blockedSteps });

    public static ChangeEvent Binding(PipelineConfigBinding binding, bool deleted = false)
        => new(binding.PipelineId, new Contract.BindingChanged
        {
            PipelineId = binding.PipelineId.Value.Value,
            State = deleted ? Contract.BindingChangeState.Removed
                : binding.Status.InProgress ? Contract.BindingChangeState.Reconciling
                : binding.Status.Result switch
                {
                    ReconcileResult.Success => Contract.BindingChangeState.Success,
                    ReconcileResult.Error => Contract.BindingChangeState.Error,
                    _ => Contract.BindingChangeState.NeverRun,
                },
        });

    public static ChangeEvent Config(ConfigSnapshot snapshot)
        => new(snapshot.PipelineId, new Contract.ConfigChanged { PipelineId = snapshot.PipelineId.Value.Value, Snapshot = snapshot.Number });

    public static ChangeEvent Pending(Id<Pipeline> pipelineId, PendingVersion? pending)
        => new(pipelineId, new Contract.PendingChanged { PipelineId = pipelineId.Value.Value, Version = pending?.VersionNumber });

    public static ChangeEvent Queued(Id<Pipeline> pipelineId, bool queued)
        => new(pipelineId, new Contract.QueuedChanged { PipelineId = pipelineId.Value.Value, Queued = queued });

    public static ChangeEvent Secrets(Id<Pipeline> pipelineId)
        => new(pipelineId, new Contract.SecretsChanged { PipelineId = pipelineId.Value.Value });

    public static ChangeEvent Poll(Id<Pipeline> pipelineId)
        => new(pipelineId, new Contract.PollChanged { PipelineId = pipelineId.Value.Value });

    public static ChangeEvent Pipeline(Id<Pipeline> pipelineId, string change)
        => new(pipelineId, new Contract.PipelineChanged { PipelineId = pipelineId.Value.Value, Change = change });

    private static ChangeEvent Version(Id<Pipeline> pipelineId, int? version, string change)
        => new(pipelineId, new Contract.VersionChanged { PipelineId = pipelineId.Value.Value, Version = version, Change = change });
}
