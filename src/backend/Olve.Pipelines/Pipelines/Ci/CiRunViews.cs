using System.Text.Json;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>CI runs and pull requests as the API shows them. Transient.</summary>
public class CiRunViewService(
    CiRunService runs,
    CiAttemptService attempts,
    CiPlanCache plans,
    PipelineVersionService versions,
    EntityStore<PipelineConfigBinding> bindings,
    CiQueueService queue,
    PreviewService previews)
{
    public Contract.CiRunSummary Summarize(CiRun run)
    {
        var steps = plans.Get(run).TryPickProblems(out _, out var plan)
            ? []
            : Stages(run, plan).SelectMany(s => s.Steps.Select(step => new Contract.CiStepState { Name = step.Name, Stage = s.Index, Status = step.Status })).ToArray();
        return new Contract.CiRunSummary
        {
            Id = run.Id.Value.Value,
            Number = run.Number,
            Kind = run.Kind switch
            {
                CiRunKind.Queue => Contract.CiRunKind.Queue,
                CiRunKind.Close => Contract.CiRunKind.Close,
                _ => Contract.CiRunKind.Pull,
            },
            Status = PipelineViews.ToApi(run.Status),
            Pulls = run.IsQueue ? [.. (run.Batch ?? []).Select(p => p.Number)] : [run.Pull],
            TestedSha = run.TestedSha,
            Tree = run.Tree,
            MainSha = run.MainSha,
            CreatedAt = run.CreatedAt,
            FinishedAt = run.FinishedAt,
            SupersededByNumber = run.SupersededByNumber,
            CancelReason = run.CancelReason switch
            {
                CiCancelReason.Closed => Contract.CiCancelReason.Closed,
                CiCancelReason.Merged => Contract.CiCancelReason.Merged,
                CiCancelReason.Dequeued => Contract.CiCancelReason.Dequeued,
                _ => null,
            },
            Steps = steps,
        };
    }

    public Contract.CiRun Detail(CiRun run)
    {
        var s = Summarize(run);
        var stages = plans.Get(run).TryPickProblems(out _, out var plan) ? [] : Stages(run, plan);
        return new Contract.CiRun
        {
            Id = s.Id,
            Number = s.Number,
            Kind = s.Kind,
            Status = s.Status,
            Pulls = s.Pulls,
            TestedSha = s.TestedSha,
            Tree = s.Tree,
            MainSha = s.MainSha,
            CreatedAt = s.CreatedAt,
            FinishedAt = s.FinishedAt,
            SupersededByNumber = s.SupersededByNumber,
            CancelReason = s.CancelReason,
            Steps = s.Steps,
            PullHeads = run.IsQueue
                ? [.. (run.Batch ?? []).Select(p => new Contract.CiRunPull { Number = p.Number, Title = p.Title, Branch = p.Branch, HeadSha = p.HeadSha })]
                : [new Contract.CiRunPull { Number = run.Pull, Title = run.Title, Branch = run.Branch, HeadSha = run.HeadSha }],
            DefinitionSha = run.OwnDefinition ? run.TestedSha : versions.TryGet(run.VersionId, out var version) && version.CommitSha is { } built ? built : run.MainSha,
            Stages = stages,
            Problems = run.Problems ?? [],
        };
    }

    public Contract.Pull Pull(PullRequest pull)
    {
        var latest = runs.ListByPull(pull.PipelineId, pull.Number).LastOrDefault();
        return new Contract.Pull
        {
            Number = pull.Number,
            Title = pull.Title,
            Branch = pull.Branch,
            HeadSha = pull.HeadSha,
            Draft = pull.Draft,
            State = pull.State switch
            {
                PullRequestState.Merged => Contract.PullState.Merged,
                PullRequestState.Closed => Contract.PullState.Closed,
                _ => Contract.PullState.Open,
            },
            Conflict = pull.Mergeable == false,
            Stale = latest is not null && BranchHead(pull.PipelineId) is { } head && latest.MainSha != head,
            LatestRun = latest is null ? null : Summarize(latest),
            Queue = queue.Latest(pull.PipelineId, pull.Number) is { } entry ? Entry(entry) : null,
            Preview = previews.TryGet(pull.PipelineId, pull.Number, out var preview) ? Preview(preview) : null,
            UpdatedAt = pull.UpdatedAt,
        };
    }

    public Contract.PullDetail PullDetail(PullRequest pull)
    {
        var view = Pull(pull);
        return new Contract.PullDetail
        {
            Number = view.Number,
            Title = view.Title,
            Branch = view.Branch,
            HeadSha = view.HeadSha,
            Draft = view.Draft,
            State = view.State,
            Conflict = view.Conflict,
            Stale = view.Stale,
            LatestRun = view.LatestRun,
            Queue = view.Queue,
            Preview = view.Preview,
            UpdatedAt = view.UpdatedAt,
            Runs = [.. runs.ListByPull(pull.PipelineId, pull.Number)
                .Concat(runs.ListCloseRuns(pull.PipelineId, pull.Number))
                .Concat(runs.ListByPipeline(pull.PipelineId).Where(r => r.IsQueue && (r.Batch ?? []).Any(p => p.Number == pull.Number)))
                .OrderByDescending(r => r.Number).Select(Summarize)],
        };
    }

    /// <summary>The merge queue: unfinished entries in order, then those finished within a day, newest first.</summary>
    public Contract.Queue Queue(Id<Pipeline> pipelineId)
    {
        var all = queue.ListByPipeline(pipelineId);
        return new Contract.Queue
        {
            Entries = [.. all.Where(e => !e.IsFinished).Select(Entry)],
            Finished = [.. all.Where(e => e.IsFinished).OrderByDescending(e => e.FinishedAt).ThenByDescending(e => e.Sequence).Select(Entry)],
        };
    }

    public static Contract.Preview Preview(Preview preview) => new()
    {
        State = preview.State switch
        {
            PreviewState.Live => Contract.PreviewState.Live,
            PreviewState.Removing => Contract.PreviewState.Removing,
            PreviewState.Removed => Contract.PreviewState.Removed,
            PreviewState.Failing => Contract.PreviewState.Failing,
            _ => Contract.PreviewState.Deploying,
        },
        Links = RunViews.ToApi(preview.Links) ?? [],
        DeployedBy = preview.DeployedByNumber,
        DeployedAt = preview.DeployedAt,
    };

    public Contract.QueueEntry Entry(CiQueueEntry entry)
    {
        var position = entry.IsFinished ? (int?)null : queue.Unfinished(entry.PipelineId).ToList().FindIndex(e => e.Id == entry.Id) + 1;
        return new Contract.QueueEntry
        {
            Pull = entry.Pull,
            Title = entry.Title,
            Branch = entry.Branch,
            HeadSha = entry.HeadSha,
            State = entry.State switch
            {
                CiQueueState.Testing => Contract.QueueEntryState.Testing,
                CiQueueState.Merged => Contract.QueueEntryState.Merged,
                CiQueueState.Removed => Contract.QueueEntryState.Removed,
                _ => Contract.QueueEntryState.Queued,
            },
            Position = position is > 0 ? position : null,
            EnqueuedAt = entry.EnqueuedAt,
            Via = entry.Via == CiQueueVia.AutoMerge ? Contract.QueueEntryVia.AutoMerge : Contract.QueueEntryVia.Api,
            EnqueuedBy = entry.EnqueuedBy,
            Runs = entry.Runs,
            FinishedAt = entry.FinishedAt,
            Reason = entry.Reason,
            FailedStep = entry.FailedStep,
        };
    }

    public static Contract.CiAttempt ToApi(CiAttempt a) => new()
    {
        Id = a.Id.Value.Value,
        RunNumber = a.RunNumber,
        StepName = a.StepName,
        Number = a.Number,
        Status = RunViews.ToApi(a.Status),
        CreatedAt = a.CreatedAt,
        Output = a.Output is null ? null : new Contract.AttemptOutput { Key = a.Output.Key, Json = a.Output.Json },
        Input = a.Input,
        Timings = a.Timings is null ? null : RunViews.ToApi(a.Timings),
        Links = RunViews.ToApi(a.Links),
        ReusedFrom = ToApi(a.ReusedFrom),
    };

    public static Contract.CiAttemptRef? ToApi(CiReuse? reuse) =>
        reuse is null ? null : new Contract.CiAttemptRef { Run = reuse.Run, Step = reuse.Step, Attempt = reuse.Attempt };

    /// <summary>The branch head the binding last read: what "stale" compares a run's base against.</summary>
    private string? BranchHead(Id<Pipeline> pipelineId) =>
        bindings.List().FirstOrDefault(b => b.PipelineId == pipelineId) is { } binding ? binding.Head?.Sha ?? binding.LastPushSha : null;

    private Contract.CiRunStage[] Stages(CiRun run, RunPlan plan)
    {
        using var document = JsonDocument.Parse(run.Input);
        var input = document.RootElement.Clone();
        var byStep = attempts.ListByRun(run.Id).GroupBy(a => a.StepName).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Number).ToList());

        return [.. plan.Stages.Select(stage => new Contract.CiRunStage
        {
            Index = stage.Index,
            When = [.. stage.Conditions.SelectMany(c => c.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
                .Select(f => new Contract.RunStageCondition { Field = f.Key, Values = [.. f.Value] })],
            Steps = [.. stage.Steps.Select(step =>
            {
                var excluded = stage.EvaluateWhen(input);
                var units = stage.Units.Where(u => ReferenceEquals(u.Step, step))
                    .Select(u => Unit(run, byStep.TryGetValue(u.Name, out var l) ? l : [], excluded))
                    .ToList();
                if (step.Matrix is null)
                    return units[0].Step(step.Name);

                var cells = stage.Units.Where(u => ReferenceEquals(u.Step, step)).Zip(units, (u, state) => state.Cell(u)).ToArray();
                return new Contract.CiRunStep
                {
                    Name = step.Name,
                    Status = MatrixRollUp.Status([.. cells.Select(c => c.Status)]),
                    Reason = MatrixRollUp.Reason([.. stage.Units.Where(u => ReferenceEquals(u.Step, step)).Zip(cells, (u, c) => (u.Cell!, c.Status, c.Reason))]),
                    AttemptCount = cells.Sum(c => c.AttemptCount),
                    LatestAttempt = null,
                    SucceededAttempt = null,
                    StartedAt = cells.Min(c => c.StartedAt),
                    FinishedAt = cells.All(c => c.FinishedAt is not null) ? cells.Max(c => c.FinishedAt) : null,
                    ReusedFrom = null,
                    Cells = cells,
                };
            })],
        })];
    }

    private static UnitState Unit(CiRun run, List<CiAttempt> list, string? excluded)
    {
        if (excluded is not null)
            return new UnitState(Contract.RunStepStatus.Excluded, excluded, [], null);
        var latest = list.LastOrDefault();
        var status = latest?.Status.State().ToStepStatus()
                     ?? (run.Status is RunStatus.Running ? Contract.RunStepStatus.Pending : Contract.RunStepStatus.NotRun);
        return new UnitState(status, latest?.Status switch
        {
            Skipped s => s.Reason,
            Failed f => f.Reason,
            Cancelled c => c.Reason,
            _ => null,
        }, list, latest);
    }

    private sealed record UnitState(Contract.RunStepStatus Status, string? Reason, List<CiAttempt> Attempts, CiAttempt? Latest)
    {
        private CiAttempt? Succeeded => Attempts.LastOrDefault(a => a.Status is AttemptStatus.Succeeded);

        public Contract.CiRunStep Step(string name) => new()
        {
            Name = name,
            Status = Status,
            Reason = Reason,
            AttemptCount = Attempts.Count,
            LatestAttempt = Latest?.Number,
            SucceededAttempt = Succeeded?.Number,
            StartedAt = Latest?.StartedAt(),
            FinishedAt = Latest?.FinishedAt(),
            Phases = Latest is null ? null : Phases(Latest),
            Links = RunViews.ToApi((Succeeded ?? Latest)?.Links),
            ReusedFrom = ToApi(Latest?.ReusedFrom),
            Cells = null,
        };

        public Contract.CiRunStepCell Cell(PlanUnit unit) => new()
        {
            Name = unit.Name,
            Cell = unit.Cell!.Values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal),
            Status = Status,
            Reason = Reason,
            AttemptCount = Attempts.Count,
            LatestAttempt = Latest?.Number,
            SucceededAttempt = Succeeded?.Number,
            StartedAt = Latest?.StartedAt(),
            FinishedAt = Latest?.FinishedAt(),
            Phases = Latest is null ? null : Phases(Latest),
            Links = RunViews.ToApi((Succeeded ?? Latest)?.Links),
            ReusedFrom = ToApi(Latest?.ReusedFrom),
        };
    }

    /// <summary>Where a CI attempt's time went (no preflight, no input wait).</summary>
    private static Contract.AttemptPhases? Phases(CiAttempt attempt)
    {
        if (attempt.ReusedFrom is not null || attempt.StartedAt() is not { } started)
            return null;
        var t = attempt.Timings;
        return StepViews.ToApi(new AttemptPhases(
            Math.Max(0, (started - attempt.CreatedAt).TotalSeconds),
            Seconds(started, t?.InitStartedAt ?? t?.StepStartedAt),
            Seconds(t?.InitStartedAt, t?.InitFinishedAt),
            Seconds(t?.InitFinishedAt, t?.StepStartedAt),
            Seconds(t?.StepStartedAt, t?.StepFinishedAt),
            Seconds(t?.ObservedAt ?? t?.StepFinishedAt, attempt.FinishedAt()),
            Seconds(t?.StepFinishedAt, t?.ObservedAt)));
    }

    private static double? Seconds(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? Math.Max(0, (b - a).TotalSeconds) : null;
}
