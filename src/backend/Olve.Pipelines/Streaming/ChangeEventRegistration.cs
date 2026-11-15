using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Streaming;

/// <summary>Domain events → <c>/api/events</c> notifications (one line each).</summary>
public class ChangeEventRegistration(
    PipelineEvents pipelineEvents,
    RunEvents runEvents,
    AttemptEvents attemptEvents,
    PipelineVersionEvents versionEvents,
    PipelineConfigBindingEvents bindingEvents,
    AttachmentStore<Pipeline, PipelineActivation> activations,
    AttachmentStore<Pipeline, PipelineBlockedSteps> blocked,
    AttachmentStore<Pipeline, PendingVersion> pendingVersions,
    AttachmentStore<Pipeline, PendingRunStart> queuedStarts,
    AttachmentStore<Pipeline, PollTriggerStates> pollStates,
    EntityStore<ConfigSnapshot> configSnapshots,
    PipelineSecretEvents secretEvents,
    PullRequestEvents pullEvents,
    CiRunEvents ciRunEvents,
    CiAttemptEvents ciAttemptEvents,
    CiQueueEvents ciQueueEvents,
    ChangeBroadcaster broadcaster) : IRunOnStartup
{
    public Result Run()
    {
        pipelineEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Pipeline(e.Id, "added")));
        pipelineEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.Pipeline(e.Id, "updated")));
        pipelineEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.Pipeline(e.Id, "removed")));

        runEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Run(e.Entity)));
        runEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.Run(e.After)));
        runEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.Run(e.Entity, deleted: true)));

        attemptEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Attempt(e.Entity)));
        attemptEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.Attempt(e.After)));

        versionEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.VersionAdded(e.Entity)));
        versionEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.VersionDeleted(e.Entity)));
        activations.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.Activation(id, activations.TryGet(id, out var a) ? a : null)));

        blocked.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.BlockedSteps(id, blocked.TryGet(id, out var b) ? b.BlockedSteps : [])));
        blocked.OnRemoved.Subscribe(id => broadcaster.Publish(ChangeEvent.BlockedSteps(id, [])));

        bindingEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Binding(e.Entity)));
        bindingEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.Binding(e.After)));
        bindingEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.Binding(e.Entity, deleted: true)));

        configSnapshots.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Config(e.Entity)));
        pendingVersions.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.Pending(id, pendingVersions.TryGet(id, out var p) ? p : null)));
        pendingVersions.OnRemoved.Subscribe(id => broadcaster.Publish(ChangeEvent.Pending(id, null)));
        queuedStarts.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.Queued(id, true)));
        queuedStarts.OnRemoved.Subscribe(id => broadcaster.Publish(ChangeEvent.Queued(id, false)));
        pollStates.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.Poll(id)));
        secretEvents.OnChanged.Subscribe(id => broadcaster.Publish(ChangeEvent.Secrets(id)));

        pullEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Pull(e.Entity)));
        pullEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.Pull(e.After)));
        pullEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.Pull(e.Entity)));

        ciRunEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.CiRun(e.Entity)));
        ciRunEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.CiRun(e.After)));
        ciRunEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.CiRun(e.Entity, deleted: true)));
        ciAttemptEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.CiAttempt(e.Entity)));
        ciAttemptEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.CiAttempt(e.After)));
        ciQueueEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.CiQueue(e.Entity.PipelineId)));
        ciQueueEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.CiQueue(e.After.PipelineId)));
        ciQueueEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.CiQueue(e.Entity.PipelineId)));

        return Result.Success();
    }
}
