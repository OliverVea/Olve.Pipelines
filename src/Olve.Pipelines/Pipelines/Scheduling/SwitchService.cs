using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Drives a version switch (spec §6.2): <c>Active(7)</c> → <c>Draining(7 → 8)</c> → <c>Active(8)</c>.
/// <list type="bullet">
/// <item>While draining, runs on other versions start no new attempts (the scheduler checks the active
/// version) and new run requests queue (<see cref="RunStartService"/>).</item>
/// <item>The drain completes when no attempt of a run pinned to another version than the target is
/// Scheduled/InProgress. With <c>cancelRunning</c> those runs and attempts are cancelled instead of
/// awaited. Paused/held runs have no active attempts, so they never block.</item>
/// <item>On completion, Running runs left on other versions (paused, held, stopped mid-way by the
/// drain) are cancelled — they are un-continuable on the new version — and the queued request, if any,
/// starts on the new version (<see cref="OnSwitchCompleted"/>).</item>
/// </list>
/// <see cref="TryCompleteDrain"/> runs after every attempt finishes (under the scheduling lock, after
/// that run was advanced) and at startup. Transient.
/// </summary>
public class SwitchService(
    PipelineActivationService activations,
    RunService runs,
    AttemptService attempts,
    AttachmentStore<Pipeline, PendingRunStart> pending,
    RunStartService starts,
    RunSchedulingLock schedulingLock,
    ILogger<SwitchService> logger)
{
    /// <summary>Begins (or retargets/rolls back) a switch and completes it at once if nothing is in flight.</summary>
    public Result<PipelineActivation> Switch(Id<Pipeline> pipelineId, Id<PipelineVersion> toVersionId, bool cancelRunning = false)
        => schedulingLock.Run<Result<PipelineActivation>>(() =>
        {
            if (activations.BeginSwitch(pipelineId, toVersionId, cancelRunning).TryPickProblems(out var problems, out var next))
                return problems;

            if (next is PipelineActivation.Active)
                OnSwitchCompleted(pipelineId); // first version, or rollback while draining
            else
                TryCompleteDrain(pipelineId);

            return activations.TryGet(pipelineId, out var current) ? current : next;
        });

    /// <summary>Completes a drain when nothing on the old version(s) is in flight. Returns true when the pipeline is now Active.</summary>
    public bool TryCompleteDrain(Id<Pipeline> pipelineId) => schedulingLock.Run(() =>
    {
        if (!activations.TryGet(pipelineId, out var state) || state is not PipelineActivation.Draining draining)
            return state is PipelineActivation.Active;

        var inFlight = InFlightOffVersion(pipelineId, draining.ToVersionId);
        if (inFlight.Count > 0)
        {
            if (!draining.CancelRunning)
                return false;

            CancelOffVersion(pipelineId, draining, inFlight);

            // Cancelling fires attempt events that may already have completed the switch (re-entrantly).
            if (!activations.TryGet(pipelineId, out state) || state is not PipelineActivation.Draining stillDraining)
                return state is PipelineActivation.Active;
            if (stillDraining != draining || InFlightOffVersion(pipelineId, draining.ToVersionId).Count > 0)
                return false;
        }

        if (activations.CompleteSwitch(pipelineId).TryPickProblems(out var problems, out var completed))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Could not complete the switch of pipeline '{PipelineId}'", pipelineId);
            return false;
        }

        logger.LogInformation("Pipeline '{PipelineId}' switched from version {FromVersion} to {ToVersion}",
            pipelineId, draining.FromVersionNumber, draining.ToVersionNumber);

        AbandonOffVersion(pipelineId, ((PipelineActivation.Active)completed).VersionId);
        OnSwitchCompleted(pipelineId);
        return true;
    });

    /// <summary>The hook run when a pipeline becomes Active: starts the request queued during the drain.</summary>
    public void OnSwitchCompleted(Id<Pipeline> pipelineId) => schedulingLock.Run(() =>
    {
        if (!pending.TryGet(pipelineId, out var queued))
            return;

        pending.Remove(pipelineId);
        if (starts.StartQueued(pipelineId, queued).TryPickProblems(out var problems, out var outcome))
            logger.LogProblems(LogLevel.Warning, problems, "Could not start the queued {TriggerKind} run of pipeline '{PipelineId}'",
                queued.Trigger.Kind, pipelineId);
        else
            logger.LogInformation("Started the {TriggerKind} run queued during the switch of pipeline '{PipelineId}': {Outcome}",
                queued.Trigger.Kind, pipelineId, outcome.GetType().Name);
    });

    private List<Attempt> InFlightOffVersion(Id<Pipeline> pipelineId, Id<PipelineVersion> targetVersionId)
    {
        var offVersionRuns = runs.ListByPipeline(pipelineId).Where(r => r.VersionId != targetVersionId).Select(r => r.Id).ToHashSet();
        return attempts.ListByPipeline(pipelineId).Where(a => a.Status.IsActive() && offVersionRuns.Contains(a.RunId)).ToList();
    }

    private void CancelOffVersion(Id<Pipeline> pipelineId, PipelineActivation.Draining draining, List<Attempt> inFlight)
    {
        logger.LogInformation("Cancelling {Count} in-flight attempt(s) of pipeline '{PipelineId}' to switch to version {ToVersion}",
            inFlight.Count, pipelineId, draining.ToVersionNumber);

        // Runs first: a terminal run ignores its attempts' cancellation.
        foreach (var run in runs.ListByPipeline(pipelineId).Where(r => r.Status is RunStatus.Running && r.VersionId != draining.ToVersionId))
        {
            if (runs.Cancel(run.Id).TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Debug, problems, "Could not cancel run #{RunNumber} ('{RunId}')", run.Number, run.Id);
        }

        foreach (var attempt in inFlight)
        {
            if (attempts.Cancel(attempt.Id).TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Debug, problems, "Could not cancel attempt '{AttemptId}' of step '{StepName}'", attempt.Id, attempt.StepName);
        }
    }

    private void AbandonOffVersion(Id<Pipeline> pipelineId, Id<PipelineVersion> activeVersionId)
    {
        foreach (var run in runs.ListByPipeline(pipelineId).Where(r => r.Status is RunStatus.Running && r.VersionId != activeVersionId))
        {
            if (!runs.Cancel(run.Id).TryPickProblems(out _))
                logger.LogInformation("Cancelled run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}': its version {VersionNumber} is no longer active",
                    run.Number, run.Id, pipelineId, run.VersionNumber);
        }
    }
}
