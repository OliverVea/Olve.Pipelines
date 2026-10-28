using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>The outcome of a run start request.</summary>
public abstract record RunStartOutcome
{
    /// <summary>A new run was created on the active version (its first stage is already scheduled).</summary>
    public sealed record Started(Run Run) : RunStartOutcome;

    /// <summary>An automatic trigger matched an existing run (spec §5.3); nothing was created.</summary>
    public sealed record Deduplicated(Run Existing) : RunStartOutcome;

    /// <summary>The pipeline is draining; the request replaced any earlier queued one and starts when the switch completes.</summary>
    public sealed record Queued(PendingRunStart Pending) : RunStartOutcome;
}

/// <summary>
/// Starts runs (spec §5.2, §5.3, §6.2).
/// <list type="bullet">
/// <item><b>Version.</b> A run pins the pipeline's Active version. While the pipeline is Draining the
/// request is queued latest-wins (one <see cref="PendingRunStart"/> per pipeline) and started by
/// <see cref="SwitchService.OnSwitchCompleted"/> on the new version. No version → error.</item>
/// <item><b>De-dup</b> (automatic triggers only — Push/Poll): the key is the trigger's
/// <see cref="RunTrigger.DedupKey"/>, defaulting to the canonical hash of the input (the mapped input).
/// Only runs pinned to the version a new run would pin (Active, or the drain target) count. A request
/// is a duplicate when such a Running run has the same key, or when the most recent such automatic run
/// has the same key, was not cancelled and was created within <see cref="DedupWindow"/>. Comparing with
/// the latest automatic run only (not any older one) keeps A → B → A deploying A again.</item>
/// <item><b>Concurrent runs.</b> Starting a run leaves older runs running; they meet only per step
/// (one InProgress attempt per step, latest-wins among Scheduled ones, see <see cref="Jobs.JobObsoletionService"/>).
/// Explicit (manual) runs and whole-run reruns are never de-duplicated.</item>
/// </list>
/// <item><b>Config.</b> A run pins the latest config snapshot and records its effective config for the
/// version (defaults applied). A held active version (a secret its config references is unset) starts nothing.</item>
/// Input is assumed validated against the input model already (B7); it is stored verbatim.
/// </summary>
public class RunStartService(
    EntityStore<Pipeline> pipelines,
    RunService runs,
    PipelineActivationService activations,
    AttachmentStore<Pipeline, PendingRunStart> pending,
    AttachmentStore<Pipeline, PendingVersion> pendingVersions,
    ConfigSnapshotService configSnapshots,
    RunPlanCache plans,
    RunSchedulingLock schedulingLock,
    TimeProvider timeProvider,
    ILogger<RunStartService> logger)
{
    /// <summary>How long a finished automatic run still de-duplicates an identical automatic trigger.</summary>
    public static readonly TimeSpan DedupWindow = TimeSpan.FromHours(1);

    public Result<RunStartOutcome> Start(Id<Pipeline> pipelineId, string inputJson, RunTrigger trigger)
        => StartCore(pipelineId, inputJson, trigger, rerunOf: null);

    /// <summary>A new run with the same input as <paramref name="runId"/>, linked via <see cref="Run.RerunOf"/>.
    /// Explicit: bypasses de-dup and supersedes nothing. <paramref name="inputJson"/> replaces the original input
    /// when given (the original re-validated against the version the rerun binds to, defaults filled in).</summary>
    public Result<RunStartOutcome> RerunRun(Id<Run> runId, string? inputJson = null) => schedulingLock.Run(() =>
    {
        if (!runs.TryGet(runId, out var original))
            return new NotFoundProblem("Run '{0}' not found.", runId);

        return StartCore(original.PipelineId, inputJson ?? original.Input, new RunTrigger(RunTriggerKind.Manual), original.Id);
    });

    /// <summary>Starts a request that was queued while draining (called on switch completion).</summary>
    internal Result<RunStartOutcome> StartQueued(Id<Pipeline> pipelineId, PendingRunStart queued)
        => StartCore(pipelineId, queued.Input, queued.Trigger, queued.RerunOf);

    private Result<RunStartOutcome> StartCore(Id<Pipeline> pipelineId, string inputJson, RunTrigger trigger, Id<Run>? rerunOf)
        => schedulingLock.Run<Result<RunStartOutcome>>(() =>
    {
        if (!pipelines.Contains(pipelineId))
            return new NotFoundProblem("Pipeline '{0}' not found.", pipelineId);
        if (!StageScheduler.TryParseInput(inputJson, out var input))
            return new ResultProblem("Run input must be a JSON object.");

        if (trigger.IsAutomatic && trigger.DedupKey is null)
            trigger = trigger with { DedupKey = CanonicalJson.Hash(input) };

        if (!activations.TryGet(pipelineId, out var activation))
            return new ConflictProblem("Pipeline '{0}' has no active version.", pipelineId);

        // De-dup only against runs of the version a new run would pin: during a drain the old
        // version's runs are about to be abandoned, so they must not swallow the trigger.
        var targetVersionId = activation switch
        {
            PipelineActivation.Draining d => d.ToVersionId,
            PipelineActivation.Active a => a.VersionId,
            _ => throw new InvalidOperationException($"Unknown activation state '{activation.GetType().Name}'."),
        };
        if (trigger.IsAutomatic && FindDuplicate(pipelineId, targetVersionId, trigger.DedupKey!) is { } duplicate)
        {
            logger.LogInformation(
                "Ignoring {TriggerKind} trigger of pipeline '{PipelineId}': duplicate of run #{RunNumber} ('{RunId}')",
                trigger.Kind, pipelineId, duplicate.Number, duplicate.Id);
            return new RunStartOutcome.Deduplicated(duplicate);
        }

        if (activation is PipelineActivation.Draining draining)
        {
            var queued = new PendingRunStart(inputJson, trigger, timeProvider.GetUtcNow(), rerunOf);
            pending.Set(pipelineId, queued);
            logger.LogInformation(
                "Queued {TriggerKind} run start of pipeline '{PipelineId}' until the switch from version {FromVersion} to {ToVersion} completes",
                trigger.Kind, pipelineId, draining.FromVersionNumber, draining.ToVersionNumber);
            return new RunStartOutcome.Queued(queued);
        }

        var active = (PipelineActivation.Active)activation;
        if (pendingVersions.TryGet(pipelineId, out var held) && held.VersionId == active.VersionId)
            return new ConflictProblem("Version {0} is held until its config is satisfied: {1}.", active.VersionNumber, string.Join("; ", held.Reasons));
        if (PinConfig(pipelineId, active).TryPickProblems(out var configProblems, out var pinned))
            return configProblems;
        if (runs.Create(pipelineId, active.VersionId, inputJson, trigger, rerunOf, pinned.Snapshot, pinned.Config).TryPickProblems(out var problems, out var run))
            return problems;

        logger.LogInformation("Started run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' on version {VersionNumber} ({TriggerKind})",
            run.Number, run.Id, pipelineId, active.VersionNumber, trigger.Kind);

        return new RunStartOutcome.Started(runs.TryGet(run.Id, out var current) ? current : run);
    });

    /// <summary>The latest snapshot and its effective config for the version; nothing when the version declares no config.</summary>
    private Result<(int? Snapshot, string? Config)> PinConfig(Id<Pipeline> pipelineId, PipelineActivation.Active active)
    {
        // An unparseable manifest fails the run in the scheduler, with the reason.
        if (plans.Get(active.VersionId).TryPickProblems(out _, out var plan) || plan.Manifest.Config.Count == 0)
            return (null, null);

        configSnapshots.TryGetLatest(pipelineId, out var snapshot);
        if (ConfigValidation.Effective(plan.Manifest, configSnapshots.Document(snapshot)).TryPickProblems(out var invalid, out var effective))
            return new ConflictProblem("Config {0} does not satisfy version {1}: {2}.",
                snapshot is null ? "(none saved)" : $"#{snapshot.Number}", active.VersionNumber, string.Join("; ", invalid.Select(p => p.ToDisplayString())));
        return (snapshot?.Number, effective.ToJsonString());
    }

    private Run? FindDuplicate(Id<Pipeline> pipelineId, Id<PipelineVersion> versionId, string key)
    {
        var all = runs.ListByPipeline(pipelineId).Where(r => r.VersionId == versionId).ToList(); // newest first
        if (all.FirstOrDefault(r => r.Status is RunStatus.Running && r.Trigger.DedupKey == key) is { } running)
            return running;

        var latestAutomatic = all.FirstOrDefault(r => r.Trigger.IsAutomatic);
        return latestAutomatic is not null
               && latestAutomatic.Status is not RunStatus.Cancelled
               && latestAutomatic.Trigger.DedupKey == key
               && latestAutomatic.CreatedAt >= timeProvider.GetUtcNow() - DedupWindow
            ? latestAutomatic
            : null;
    }
}
