using System.Text.Json;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Utilities.Paginations;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// Read models of the run API (spec §5, §7): run list, run detail with the pinned stage layout, step
/// history by identity. Stateless queries over runs, attempts, versions, blocked steps and activation. Transient.
/// </summary>
public class RunViewService(
    RunService runs,
    AttemptService attempts,
    PipelineVersionService versions,
    PipelineActivationService activations,
    RunPlanCache plans,
    StepBlockService blocks)
{
    public Page<RunSummary> List(Id<Pipeline> pipelineId, ListRunsRequest request)
    {
        var all = runs.ListByPipeline(pipelineId); // newest first
        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var items = all.Skip(page * pageSize).Take(pageSize).Select(Summarize).ToList();
        return new Page<RunSummary>(items, page, pageSize, all.Count);
    }

    public RunSummary Summarize(Run run) => new(
        run.Id,
        run.Number,
        run.Status,
        run.Trigger.Kind,
        run.VersionNumber,
        run.VersionStamp,
        run.CreatedAt,
        run.FinishedAt,
        run.HeldAt,
        run.SupersededBy is { } s && runs.TryGet(s, out var newer) ? newer.Number : null,
        run.RerunOf is { } r && runs.TryGet(r, out var original) ? original.Number : null);

    public Result<RunDetail> Get(Id<Pipeline> pipelineId, int number)
    {
        if (!runs.TryGetByNumber(pipelineId, number, out var run))
            return new NotFoundProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId);

        versions.TryGet(run.VersionId, out var version);
        var continuable = run.Status is not RunStatus.Superseded
                          && activations.TryGet(pipelineId, out var activation)
                          && activation is PipelineActivation.Active active
                          && active.VersionId == run.VersionId;

        JsonElement input;
        using (var doc = JsonDocument.Parse(run.Input))
            input = doc.RootElement.Clone();

        var stages = plans.Get(run.VersionId).TryPickProblems(out _, out var plan)
            ? []
            : BuildStages(run, input, plan, version);

        return new RunDetail(Summarize(run), run.VersionId, version?.CommitSha, input, continuable, stages);
    }

    private RunStageView[] BuildStages(Run run, JsonElement input, RunPlan plan, PipelineVersion? version)
    {
        var byStep = attempts.ListByRun(run.Id).GroupBy(a => a.StepName).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Number).ToList());
        var blocked = blocks.ListBlocked(run.PipelineId).ToHashSet(StringComparer.Ordinal);
        int? heldStage = run.HeldAt is { } held && plan.TryGetStep(held, out var hs, out _) ? hs.Index : null;

        return plan.Stages.Select(stage => new RunStageView(
            stage.Index,
            stage.Conditions
                .SelectMany(c => c.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
                .Select(f => new RunStageCondition(f.Key, f.Value.ToArray()))
                .ToArray(),
            stage.Steps.Select(step =>
            {
                // Not part of the run: no attempt, whatever an older scheduler recorded for it.
                if (stage.EvaluateWhen(input) is { } excluded)
                    return new RunStepView(step.Name, StepVersion(version, step.Name), "excluded", excluded, 0, null, null, null, null, blocked.Contains(step.Name));

                var list = byStep.TryGetValue(step.Name, out var l) ? l : [];
                var latest = list.LastOrDefault();
                var isBlocked = blocked.Contains(step.Name);
                var status = latest?.Status.Discriminator()
                             ?? (run.Status is RunStatus.Running && (run.HeldAt == step.Name || (isBlocked && heldStage == stage.Index))
                                 ? "held"
                                 : run.Status is RunStatus.Running ? "pending" : "not-run");
                return new RunStepView(
                    step.Name,
                    StepVersion(version, step.Name),
                    status,
                    latest is null ? null : Reason(latest.Status),
                    list.Count,
                    latest?.Number,
                    list.LastOrDefault(a => a.Status is Succeeded)?.Number,
                    latest?.StartedAt(),
                    latest?.FinishedAt(),
                    isBlocked);
            }).ToArray())).ToArray();
    }

    private static int? StepVersion(PipelineVersion? version, string stepName) =>
        version is not null && version.StepVersions.TryGetValue(stepName, out var v) ? v : null;

    /// <summary>
    /// History of one step identity across runs, newest first, with definition-change markers (spec §6.3).
    /// Runs the step was not part of (its <c>when</c> did not match) are left out.
    /// </summary>
    public IReadOnlyList<StepHistoryItem> History(Id<Pipeline> pipelineId, string stepName, int limit)
    {
        var rows = new List<StepHistoryItem>();
        var stepVersionsByVersion = new Dictionary<Id<PipelineVersion>, int?>();
        // One extra row so the oldest returned row still knows whether it changed.
        foreach (var attempt in attempts.ListByStepName(pipelineId, stepName))
        {
            if (rows.Count > limit)
                break;
            if (!runs.TryGet(attempt.RunId, out var run) || IsExcluded(run, stepName))
                continue;

            if (!stepVersionsByVersion.TryGetValue(run.VersionId, out var stepVersion))
            {
                stepVersion = versions.TryGet(run.VersionId, out var version) && version.StepVersions.TryGetValue(stepName, out var sv) ? sv : null;
                stepVersionsByVersion[run.VersionId] = stepVersion;
            }

            rows.Add(new StepHistoryItem(run.Number, run.Status, run.VersionNumber, run.VersionStamp, attempt.Id, attempt.Number,
                attempt.Status.Discriminator(), Reason(attempt.Status), attempt.CreatedAt, attempt.StartedAt(), attempt.FinishedAt(),
                stepVersion, StepVersionChanged: false));
        }

        for (var i = 0; i < rows.Count - 1; i++)
        {
            if (rows[i].StepVersion != rows[i + 1].StepVersion)
                rows[i] = rows[i] with { StepVersionChanged = true };
        }

        return rows.Take(limit).ToList();
    }

    private bool IsExcluded(Run run, string stepName)
    {
        if (plans.Get(run.VersionId).TryPickProblems(out _, out var plan) || !StageScheduler.TryParseInput(run.Input, out var input))
            return false;
        return plan.ExcludedReason(stepName, input) is not null;
    }

    private static string? Reason(AttemptStatus status) => status switch
    {
        Skipped s => s.Reason,
        Failed f => f.Reason,
        _ => null,
    };
}
