using System.Collections.Concurrent;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// Each CI definition's plan: its manifest's <c>ci</c> stages, flattened like a pipeline's. A run's definition is its
/// version's, or the build of its own tested commit. Singleton (versions and finished builds are immutable).
/// </summary>
public class CiPlanCache(RunPlanCache plans, PipelineVersionService versions, CiBuildService builds)
{
    private readonly ConcurrentDictionary<Id<PipelineVersion>, Result<RunPlan>> _plans = new();
    private readonly ConcurrentDictionary<Id<CiBuild>, Result<Manifest>> _manifests = new();
    private readonly ConcurrentDictionary<Id<CiBuild>, Result<RunPlan>> _buildPlans = new();
    private readonly ConcurrentDictionary<Id<PipelineVersion>, Result<RunPlan>> _closePlans = new();
    private readonly ConcurrentDictionary<Id<CiBuild>, Result<RunPlan>> _buildClosePlans = new();

    /// <summary>The binary the run's steps run: its version's, or its own build's.</summary>
    public bool BinaryKey(CiRun run, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? key)
    {
        if (run.OwnDefinition)
        {
            key = builds.TryGet(run.PipelineId, run.TestedSha, out var build) && build.Status is CiBuildStatus.Succeeded ? build.BinaryKey : null;
            return key is not null;
        }

        key = versions.TryGet(run.VersionId, out var version) ? version.BinaryKey : null;
        return key is not null;
    }

    /// <summary>The run's plan: its definition's CI stages, or for a close run its <c>onClose</c> stage.</summary>
    public Result<RunPlan> Get(CiRun run)
    {
        if (!run.OwnDefinition)
            return run.IsClose ? GetClose(run.VersionId) : Get(run.VersionId);
        if (!builds.TryGet(run.PipelineId, run.TestedSha, out var build) || build.Status is not CiBuildStatus.Succeeded)
            return new ConflictProblem("The pull request's definition isn't built.");
        return (run.IsClose ? _buildClosePlans : _buildPlans).GetOrAdd(build.Id, _ => Manifest(build).TryPickProblems(out var problems, out var manifest)
            ? problems
            : run.IsClose ? FromOnClose(manifest) : FromCi(manifest, "The pull request's definition declares no CI."));
    }

    private Result<RunPlan> GetClose(Id<PipelineVersion> versionId) => _closePlans.GetOrAdd(versionId, id =>
        plans.Get(id).TryPickProblems(out var problems, out var plan) ? problems : FromOnClose(plan.Manifest));

    private static Result<RunPlan> FromOnClose(Manifest manifest) =>
        manifest.Ci?.OnClose is { } onClose
            ? RunPlan.From(manifest with { Stages = [onClose] })
            : new ConflictProblem("The definition declares no onClose.");

    public Result<RunPlan> Get(Id<PipelineVersion> versionId) => _plans.GetOrAdd(versionId, id =>
        plans.Get(id).TryPickProblems(out var problems, out var plan) ? problems : FromCi(plan.Manifest, "The version declares no CI."));

    /// <summary>A succeeded build's manifest, parsed.</summary>
    public Result<Manifest> Manifest(CiBuild build) => _manifests.GetOrAdd(build.Id, _ =>
        build.Manifest is { } text ? ManifestParser.Parse(text) : new ConflictProblem("The pull request's definition isn't built."));

    private static Result<RunPlan> FromCi(Manifest manifest, string noCi)
    {
        if (manifest.Ci is not { } ci)
            return new ConflictProblem(noCi);
        if (RunPlan.HasSelect(ci.Stages))
            return new ConflictProblem("CI stages that branch on config (select) are not supported.");
        return RunPlan.From(manifest with { Stages = ci.Stages });
    }
}

/// <summary>
/// The single lock of CI scheduling (runs and attempts of every pipeline). A nested advance of a run that is being
/// advanced is deferred to the outer loop, as in <see cref="RunSchedulingLock"/>. Singleton.
/// </summary>
public sealed class CiSchedulingLock
{
    private readonly Lock _lock = new();
    private readonly HashSet<Id<CiRun>> _advancing = [];
    private readonly HashSet<Id<CiRun>> _dirty = [];
    private const int MaxPasses = 64;

    public void Run(Action action)
    {
        lock (_lock) action();
    }

    public T Run<T>(Func<T> action)
    {
        lock (_lock) return action();
    }

    public void Advance(Id<CiRun> runId, Action advance)
    {
        lock (_lock)
        {
            if (!_advancing.Add(runId))
            {
                _dirty.Add(runId);
                return;
            }

            try
            {
                var passes = 0;
                do
                {
                    _dirty.Remove(runId);
                    advance();
                } while (_dirty.Contains(runId) && ++passes < MaxPasses);
            }
            finally
            {
                _advancing.Remove(runId);
                _dirty.Remove(runId);
            }
        }
    }
}

/// <summary>
/// Drives a CI run through its stages, like <see cref="StageScheduler"/> does a CD run: the stage barrier, <c>when</c>
/// on the CI input (excluded steps get no attempt), skip propagation and <c>retries</c>. None of CD's rules across runs
/// apply: no busy key per step, no latest-wins, no superseding of overtaken runs, no blocked steps. A step that
/// already succeeded on the same tree with the same code is reused instead of run (<see cref="CiReuseService"/>), unless it
/// declares <c>reuse: false</c>. Transient.
/// </summary>
public class CiScheduler(
    CiRunService runs,
    CiAttemptService attempts,
    CiPlanCache plans,
    CiSchedulingLock schedulingLock,
    CiReuseService reuse,
    PreviewGate previewGate,
    PreviewService previews,
    TimeProvider time,
    ILogger<CiScheduler> logger)
{
    public void Advance(Id<CiRun> runId) => schedulingLock.Advance(runId, () => AdvanceOnce(runId));

    public void AdvanceRunning() => schedulingLock.Run(() =>
    {
        foreach (var run in runs.List().Where(r => r.Status is RunStatus.Running).OrderBy(r => r.Number))
            Advance(run.Id);
    });

    private void AdvanceOnce(Id<CiRun> runId)
    {
        if (!runs.TryGet(runId, out var run) || run.Status.IsTerminal() || run.Building)
            return;

        if (plans.Get(run).TryPickProblems(out var planProblems, out var plan))
        {
            logger.LogProblems(LogLevel.Error, planProblems, "CI run #{RunNumber} of pipeline '{PipelineId}' has no usable plan in version {VersionNumber}; failing it",
                run.Number, run.PipelineId, run.VersionNumber);
            Complete(run, RunStatus.Failed);
            return;
        }

        if (!StageScheduler.TryParseInput(run.Input, out var input))
        {
            Complete(run, RunStatus.Failed);
            return;
        }

        if (run.IsClose && previewGate.LiveDeploys(run) is { Count: > 0 } live)
        {
            if (live.All(a => a.Status.IsTerminal()))
                previewGate.WhenStopped(live).ContinueWith(_ => Advance(runId), TaskScheduler.Default);
            return;
        }

        var pool = new CiRunPool(run.Id, input, plan, attempts);
        foreach (var stage in plan.Stages)
        {
            var waiting = false;
            var decisions = new Dictionary<StepDescriptor, StepDecision>(ReferenceEqualityComparer.Instance);
            StepDecision DecisionFor(StepDescriptor step) =>
                decisions.TryGetValue(step, out var d) ? d : decisions[step] = StageScheduler.Decide(stage, step, pool);

            foreach (var unit in stage.Units)
            {
                var latest = Latest(run.Id, unit.Name);
                if (latest is not null && latest.Status.IsActive())
                {
                    waiting = true;
                    continue;
                }

                var decision = DecisionFor(unit.Step);
                if (decision is StepDecision.Excluded)
                    continue;
                if (IsSettled(latest, decision) && !RetryDue(run, unit, decision))
                    continue;
                if (decision is StepDecision.Schedule && unit.Step.NeverReused && previewGate.CloseRunning(run) is not null)
                {
                    waiting = true;
                    continue;
                }

                if (decision is StepDecision.Schedule && unit.Step.NeverReused && run.Kind == CiRunKind.Pull && plan.Manifest.Ci?.OnClose is not null)
                    previews.Deploying(run, time.GetUtcNow());
                Act(run, unit, decision, plan.Manifest.CodeHash);
                if (Latest(run.Id, unit.Name) is { } created && created.Status.IsActive())
                    waiting = true;
            }

            if (waiting && run.IsQueue && FailFast(run, stage))
                return;
            if (waiting)
                return;

            var settled = stage.Units.Select(u => Latest(run.Id, u.Name)).OfType<CiAttempt>().ToList();
            if (settled.FirstOrDefault(a => a.Status is Failed) is { } failed)
            {
                logger.LogInformation("CI run #{RunNumber} of pipeline '{PipelineId}' failed at step '{StepName}'", run.Number, run.PipelineId, failed.StepName);
                Complete(run, RunStatus.Failed);
                return;
            }

            if (settled.Any(a => a.Status is Cancelled))
            {
                Complete(run, RunStatus.Cancelled);
                return;
            }
        }

        logger.LogInformation("CI run #{RunNumber} of pipeline '{PipelineId}' succeeded", run.Number, run.PipelineId);
        Complete(run, RunStatus.Succeeded);
    }

    /// <summary>A queue run stops at its first failed step: the step's attempts still in flight are cancelled.</summary>
    private bool FailFast(CiRun run, PlanStage stage)
    {
        if (stage.Units.Select(u => Latest(run.Id, u.Name)).FirstOrDefault(a => a?.Status is Failed) is not { } failed)
            return false;
        foreach (var attempt in attempts.ListByRun(run.Id).Where(a => a.Status.IsActive()))
            attempts.Cancel(attempt.Id, $"cancelled: {failed.StepName} failed").DiscardResult();
        logger.LogInformation("Queue run #{RunNumber} of pipeline '{PipelineId}' failed at step '{StepName}'; its other steps are cancelled",
            run.Number, run.PipelineId, failed.StepName);
        Complete(run, RunStatus.Failed);
        return true;
    }

    private static bool IsSettled(CiAttempt? latest, StepDecision decision) => latest?.Status switch
    {
        null => false,
        Failed or Cancelled or Obsolete => true,
        Skipped skipped when latest.IsSchedulerSkip => decision is StepDecision.Skip s && s.Reason == skipped.Reason,
        Succeeded or Skipped => decision is StepDecision.Schedule sc && sc.Hash == latest.InputHash,
        _ => false,
    };

    /// <summary>A step with <c>retries: n</c> gets 1 + n consecutive tries; scheduler-side failures are never retried.</summary>
    private bool RetryDue(CiRun run, PlanUnit unit, StepDecision decision)
    {
        if (unit.Step.Retries is not > 0 || decision is not StepDecision.Schedule)
            return false;
        var failures = attempts.ListForStep(run.Id, unit.Name).Reverse().TakeWhile(a => a.Status is Failed).Count();
        if (failures == 0 || failures > unit.Step.Retries)
            return false;
        logger.LogInformation("Retrying step '{StepName}' in CI run #{RunNumber}: failure {Failures} of {MaxTries} tries",
            unit.Name, run.Number, failures, unit.Step.Retries + 1);
        return true;
    }

    private void Act(CiRun run, PlanUnit unit, StepDecision decision, string? codeHash)
    {
        var result = decision switch
        {
            StepDecision.Skip skip => attempts.RecordSkipped(run.Id, unit.Name, skip.Reason),
            StepDecision.Schedule schedule when !unit.Step.NeverReused && !run.IsClose && reuse.FindOrigin(run, codeHash, unit.Name) is { } origin =>
                attempts.RecordReused(run.Id, unit.Name, schedule.Hash, schedule.Json, origin),
            StepDecision.Schedule schedule => attempts.Schedule(run.Id, unit.Name, schedule.Hash, schedule.Json),
            StepDecision.Fail fail => attempts.RecordFailed(run.Id, unit.Name, fail.Reason, fail.Hash),
            _ => Result.Failure<CiAttempt>(new ResultProblem("Unknown decision.")),
        };
        if (result.TryPickProblems(out var problems, out var attempt))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Could not record step '{StepName}' in CI run #{RunNumber}; failing it", unit.Name, run.Number);
            if (decision is StepDecision.Schedule s)
                attempts.RecordFailed(run.Id, unit.Name, string.Join("; ", problems.Select(p => p.FormattedMessage)), s.Hash).DiscardResult();
            return;
        }

        if (attempt.ReusedFrom is { } from)
            logger.LogInformation("CI run #{RunNumber}: step '{StepName}' attempt {AttemptNumber} reuses CI run #{OriginRun}/{OriginStep}@{OriginAttempt}",
                run.Number, unit.Name, attempt.Number, from.Run, from.Step, from.Attempt);
        else
            logger.LogInformation("CI run #{RunNumber}: step '{StepName}' attempt {AttemptNumber} is {Status}",
                run.Number, unit.Name, attempt.Number, attempt.Status.Discriminator());
    }

    private void Complete(CiRun run, RunStatus status)
    {
        if (runs.Complete(run.Id, status).TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not complete CI run #{RunNumber}", run.Number);
    }

    private CiAttempt? Latest(Id<CiRun> runId, string stepName) => attempts.ListForStep(runId, stepName).LastOrDefault();
}

/// <summary>A CI run's pool read live from the attempt store.</summary>
internal sealed class CiRunPool(Id<CiRun> runId, JsonElement input, RunPlan plan, CiAttemptService attempts) : IRunPool
{
    private readonly Dictionary<Id<CiAttempt>, JsonElement?> _outputs = [];

    public JsonElement Input => input;

    public PoolEntry Get(string stepName)
    {
        if (plan.ExcludedReason(stepName, input) is { } excluded)
            return new PoolEntry.Excluded(excluded);
        if (plan.TryGetStep(stepName, out _, out var step) && step.Matrix is not null)
            return Cells(step);
        if (attempts.ListForStep(runId, stepName).LastOrDefault()?.Status is Skipped skipped)
            return new PoolEntry.Skipped(skipped.Reason);
        return attempts.TryGetLatestSucceeded(runId, stepName, out var succeeded)
            ? new PoolEntry.Produced(Output(succeeded))
            : new PoolEntry.Missing();
    }

    private PoolEntry Cells(StepDescriptor step)
    {
        var outputs = new List<CellOutput>();
        var skipped = new List<string>();
        var units = PlanStage.UnitsOf(step).ToList();
        foreach (var unit in units)
        {
            if (attempts.ListForStep(runId, unit.Name).LastOrDefault()?.Status is Skipped s)
                skipped.Add(s.Reason);
            else if (!attempts.TryGetLatestSucceeded(runId, unit.Name, out var succeeded))
                return new PoolEntry.Missing();
            else if (Output(succeeded) is { } output)
                outputs.Add(new CellOutput(unit.Cell!, output));
        }

        return skipped.Count == units.Count ? new PoolEntry.Skipped(skipped[0]) : new PoolEntry.Cells(outputs);
    }

    private JsonElement? Output(CiAttempt attempt)
    {
        if (_outputs.TryGetValue(attempt.Id, out var cached))
            return cached;
        JsonElement? output = null;
        if (attempt.Output is { } o)
        {
            using var document = JsonDocument.Parse(o.Json);
            output = document.RootElement.Clone();
        }

        _outputs[attempt.Id] = output;
        return output;
    }
}
