using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
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
    AttachmentStore<Pipeline, PipelineStepGates> gates,
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
        activations.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.Activation(id, activations.TryGet(id, out var a) ? a : null)));

        gates.OnSet.Subscribe(id => broadcaster.Publish(ChangeEvent.Gate(id, gates.TryGet(id, out var g) ? g.BlockedSteps : [])));
        gates.OnRemoved.Subscribe(id => broadcaster.Publish(ChangeEvent.Gate(id, [])));

        bindingEvents.OnAdded.Subscribe(e => broadcaster.Publish(ChangeEvent.Binding(e.Entity)));
        bindingEvents.OnUpdated.Subscribe(e => broadcaster.Publish(ChangeEvent.Binding(e.After)));
        bindingEvents.OnDeleted.Subscribe(e => broadcaster.Publish(ChangeEvent.Binding(e.Entity, deleted: true)));

        return Result.Success();
    }
}
