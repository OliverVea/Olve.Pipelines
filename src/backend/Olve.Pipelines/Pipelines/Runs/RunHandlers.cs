using System.Diagnostics;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared.Problems;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Runs;

[OperationHandler]
public sealed class ListRunsHandler(PipelineService pipelines, RunViewService views) : Contract.IRunsListHandler
{
    public Task<Contract.RunsListResponse> HandleAsync(Contract.RunsListRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        if (!pipelines.TryGet(id, out _))
            return Task.FromResult<Contract.RunsListResponse>(new Contract.RunsListResponse.NotFound(PipelineViews.NotFound(id)));
        var slice = views.List(id, request.Offset ?? 0, request.Limit ?? 50);
        return Task.FromResult<Contract.RunsListResponse>(new Contract.RunList
        {
            Items = [.. slice.Items.Select(PipelineViews.ToApi)],
            Offset = slice.Offset,
            Limit = slice.Limit,
            Total = slice.TotalCount,
        });
    }
}

/// <summary>Invalid input is a 400 listing every problem — never a half-started run.</summary>
[OperationHandler]
public sealed class StartRunHandler(RunInputService inputs, RunViewService views) : Contract.IRunsStartHandler
{
    public Task<Contract.RunsStartResponse> HandleAsync(Contract.RunsStartRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var started = inputs.StartManual(id, JsonFields.ToJsonObject(request.Body)).Bind(outcome => RunViews.ToApi(views, id, outcome));
        return Task.FromResult(started.TryPickProblems(out var problems, out var start)
            ? RunViews.Fail<Contract.RunsStartResponse>(problems, p => new Contract.RunsStartResponse.NotFound(p), p => new Contract.RunsStartResponse.Conflict(p), p => new Contract.RunsStartResponse.BadRequest(p))
            : new Contract.RunsStartResponse.Ok(start));
    }
}

[OperationHandler]
public sealed class GetCriticalPathHandler(CriticalPathService paths) : Contract.IRunsCriticalPathHandler
{
    public Task<Contract.RunsCriticalPathResponse> HandleAsync(Contract.RunsCriticalPathRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var report = paths.Get(id, request.From, request.To, request.IncludeUnsuccessful ?? false, request.SinceVersion);
        return Task.FromResult<Contract.RunsCriticalPathResponse>(report.TryPickProblems(out var problems, out var value)
            ? RunViews.Fail<Contract.RunsCriticalPathResponse>(problems, p => new Contract.RunsCriticalPathResponse.NotFound(p), null, p => new Contract.RunsCriticalPathResponse.BadRequest(p))
            : RunViews.ToApi(value));
    }
}

[OperationHandler]
public sealed class GetRunHandler(RunViewService views) : Contract.IRunsGetHandler
{
    public Task<Contract.RunsGetResponse> HandleAsync(Contract.RunsGetRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.RunsGetResponse>(views.Get(id, request.Number).TryPickProblems(out var problems, out var detail)
            ? new Contract.RunsGetResponse.NotFound(ApiProblems.Of([.. problems]))
            : RunViews.ToApi(detail));
    }
}

/// <summary>Cancels the run and its scheduled/in-progress attempts (their K8s Jobs are deleted).</summary>
[OperationHandler]
public sealed class CancelRunHandler(RunService runs, RunCancellationService cancellation, RunViewService views) : Contract.IRunsCancelHandler
{
    public Task<Contract.RunsCancelResponse> HandleAsync(Contract.RunsCancelRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var cancelled = RunViews.Find(runs, id, request.Number).Bind(run => cancellation.Cancel(run.Id)).Bind(run => views.Get(id, run.Number));
        return Task.FromResult(cancelled.TryPickProblems(out var problems, out var detail)
            ? RunViews.Fail<Contract.RunsCancelResponse>(problems, p => new Contract.RunsCancelResponse.NotFound(p), p => new Contract.RunsCancelResponse.Conflict(p), p => new Contract.RunsCancelResponse.BadRequest(p))
            : new Contract.RunsCancelResponse.Ok(RunViews.ToApi(detail)));
    }
}

/// <summary>A new run with the same input on the active version, linked via rerunOf.</summary>
[OperationHandler]
public sealed class RerunRunHandler(RunService runs, RunInputService inputs, RunViewService views) : Contract.IRunsRerunHandler
{
    public Task<Contract.RunsRerunResponse> HandleAsync(Contract.RunsRerunRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var started = RunViews.Find(runs, id, request.Number).Bind(inputs.RerunRun).Bind(outcome => RunViews.ToApi(views, id, outcome));
        return Task.FromResult(started.TryPickProblems(out var problems, out var start)
            ? RunViews.Fail<Contract.RunsRerunResponse>(problems, p => new Contract.RunsRerunResponse.NotFound(p), p => new Contract.RunsRerunResponse.Conflict(p), p => new Contract.RunsRerunResponse.BadRequest(p))
            : new Contract.RunsRerunResponse.Ok(start));
    }
}

/// <summary>Keeps completed stages, re-evaluates the rest; the input changes are merged over the stored input (only fields no completed step consumed may change).</summary>
[OperationHandler]
public sealed class ContinueRunHandler(RunService runs, RunInputService inputs, RunViewService views) : Contract.IRunsContinueHandler
{
    public Task<Contract.RunsContinueResponse> HandleAsync(Contract.RunsContinueRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        var continued = RunViews.Find(runs, id, request.Number)
            .Bind(run => inputs.Continue(run, JsonFields.ToJsonObject(request.Body.Input)))
            .Bind(run => views.Get(id, run.Number));
        return Task.FromResult(continued.TryPickProblems(out var problems, out var detail)
            ? RunViews.Fail<Contract.RunsContinueResponse>(problems, p => new Contract.RunsContinueResponse.NotFound(p), p => new Contract.RunsContinueResponse.Conflict(p), p => new Contract.RunsContinueResponse.BadRequest(p))
            : new Contract.RunsContinueResponse.Ok(RunViews.ToApi(detail)));
    }
}

[OperationHandler]
public sealed class ListRunAttemptsHandler(RunService runs, AttemptService attempts) : Contract.IRunsAttemptsHandler
{
    public Task<Contract.RunsAttemptsResponse> HandleAsync(Contract.RunsAttemptsRequest request, CancellationToken cancellationToken)
    {
        var id = PipelineViews.PipelineId(request.PipelineId);
        return Task.FromResult<Contract.RunsAttemptsResponse>(RunViews.Find(runs, id, request.Number).TryPickProblems(out var problems, out var run)
            ? new Contract.RunsAttemptsResponse.NotFound(ApiProblems.Of([.. problems]))
            : new Contract.RunsAttemptsResponse.Ok([.. attempts.ListByRun(run.Id).Select(RunViews.ToApi)]));
    }
}

/// <summary>What's running (active) or waiting for input (waiting), on this instance, oldest first, by address.</summary>
[OperationHandler]
public sealed class ListActiveAttemptsHandler(ActiveAttemptQuery query) : Contract.IAttemptsListHandler
{
    public Task<Contract.AttemptsListResponse> HandleAsync(Contract.AttemptsListRequest request, CancellationToken cancellationToken)
    {
        var list = query.List(waitingOnly: request.Status == Contract.AttemptFilter.Waiting);
        return Task.FromResult<Contract.AttemptsListResponse>(new Contract.AttemptsListResponse.Ok([.. list.Select(RunViews.ToApi)]));
    }
}

/// <summary>The contract's view of runs and attempts.</summary>
public static class RunViews
{
    public static Result<Run> Find(RunService runs, Id<Pipeline> pipelineId, int number)
        => runs.TryGetByNumber(pipelineId, number, out var run)
            ? run
            : new NotFoundProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId);

    /// <summary>The declared variant for the problems' status: 404, 409 (when declared) or 400.</summary>
    public static T Fail<T>(ResultProblemCollection problems, Func<ApiProblem, T> notFound, Func<ApiProblem, T>? conflict, Func<ApiProblem, T> badRequest)
    {
        var problem = ApiProblems.Of([.. problems]);
        return problem.Status switch
        {
            StatusCodes.Status404NotFound => notFound(problem),
            StatusCodes.Status409Conflict when conflict is not null => conflict(problem),
            _ => badRequest(problem),
        };
    }

    public static Result<Contract.RunStart> ToApi(RunViewService views, Id<Pipeline> pipelineId, RunStartOutcome outcome)
    {
        var (kind, run) = outcome switch
        {
            RunStartOutcome.Started s => (Contract.RunStartOutcome.Started, s.Run),
            RunStartOutcome.Deduplicated d => (Contract.RunStartOutcome.Deduplicated, d.Existing),
            _ => (Contract.RunStartOutcome.Queued, null),
        };
        if (run is null)
            return new Contract.RunStart { Outcome = kind, Run = null };
        return views.Get(pipelineId, run.Number).Map(detail => new Contract.RunStart { Outcome = kind, Run = ToApi(detail) });
    }

    public static Contract.Run ToApi(RunDetail d)
    {
        var r = d.Run;
        return new Contract.Run
        {
            Id = r.Id.Value.Value,
            Number = r.Number,
            Status = PipelineViews.ToApi(r.Status),
            TriggerKind = PipelineViews.ToApi(r.TriggerKind),
            VersionNumber = r.VersionNumber,
            VersionStamp = r.VersionStamp,
            CreatedAt = r.CreatedAt,
            FinishedAt = r.FinishedAt,
            HeldAt = r.HeldAt,
            SupersededByNumber = r.SupersededByNumber,
            RerunOfNumber = r.RerunOfNumber,
            ConfigSnapshot = r.ConfigSnapshot,
            VersionId = d.VersionId.Value.Value,
            CommitSha = d.CommitSha,
            Input = JsonFields.ToDictionary(d.Input),
            Continuable = d.Continuable,
            Stages = [.. d.Stages.Select(ToApi)],
            Config = d.Config,
        };
    }

    private static Contract.RunStage ToApi(RunStageView s) => new()
    {
        Index = s.Index,
        When = [.. s.When.Select(c => new Contract.RunStageCondition { Field = c.Field, Values = c.Values })],
        Steps = [.. s.Steps.Select(ToApi)],
    };

    private static Contract.RunStep ToApi(RunStepView s) => new()
    {
        Name = s.Name,
        StepVersion = s.StepVersion,
        Status = s.Status,
        Reason = s.Reason,
        AttemptCount = s.AttemptCount,
        LatestAttempt = s.LatestAttempt,
        SucceededAttempt = s.SucceededAttempt,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
        Blocked = s.Blocked,
        Phases = s.Phases is null ? null : StepViews.ToApi(s.Phases),
        WaitingFor = s.WaitingFor,
    };

    public static Contract.Attempt ToApi(Attempt a) => new()
    {
        Id = a.Id.Value.Value,
        PipelineId = a.PipelineId.Value.Value,
        RunId = a.RunId.Value.Value,
        RunNumber = a.RunNumber,
        StepName = a.StepName,
        Number = a.Number,
        Status = ToApi(a.Status),
        CreatedAt = a.CreatedAt,
        InputHash = a.InputHash,
        Output = a.Output is null ? null : new Contract.AttemptOutput { Key = a.Output.Key, Json = a.Output.Json },
        Input = a.Input,
        Timings = a.Timings is null ? null : ToApi(a.Timings),
        Answers = a.Answers?.ToDictionary(x => x.Key, x => new Contract.ExternalAnswer
        {
            Value = x.Value.Value,
            AnsweredBy = x.Value.AnsweredBy,
            AnsweredAt = x.Value.AnsweredAt,
            Source = x.Value.Source,
        }),
    };

    public static Contract.AttemptStatus ToApi(AttemptStatus s) => s switch
    {
        AttemptStatus.Waiting x => new Contract.AttemptStatusWaiting { Since = x.Since },
        AttemptStatus.Scheduled x => new Contract.AttemptStatusScheduled { SubmissionAttempts = x.SubmissionAttempts },
        AttemptStatus.InProgress x => new Contract.AttemptStatusInProgress { StartedAt = x.StartedAt },
        AttemptStatus.Succeeded x => new Contract.AttemptStatusSucceeded { StartedAt = x.StartedAt, FinishedAt = x.FinishedAt },
        AttemptStatus.Failed x => new Contract.AttemptStatusFailed { StartedAt = x.StartedAt, FinishedAt = x.FinishedAt, Reason = x.Reason },
        AttemptStatus.Skipped x => new Contract.AttemptStatusSkipped { Reason = x.Reason, StartedAt = x.StartedAt, FinishedAt = x.FinishedAt },
        AttemptStatus.Cancelled x => new Contract.AttemptStatusCancelled { StartedAt = x.StartedAt, CancelledAt = x.CancelledAt },
        AttemptStatus.Obsolete x => new Contract.AttemptStatusObsolete { SupersedingAttemptId = x.SupersedingAttemptId.Value.Value },
        _ => throw new UnreachableException(),
    };

    private static Contract.AttemptTimings ToApi(AttemptTimings t) => new()
    {
        PodCreatedAt = t.PodCreatedAt,
        InitStartedAt = t.InitStartedAt,
        InitFinishedAt = t.InitFinishedAt,
        StepStartedAt = t.StepStartedAt,
        StepFinishedAt = t.StepFinishedAt,
        ObservedAt = t.ObservedAt,
    };

    public static Contract.ActiveAttempt ToApi(ActiveAttempt a) => new()
    {
        PipelineId = a.PipelineId.Value.Value,
        PipelineName = a.PipelineName,
        Run = a.Run,
        Step = a.Step,
        Attempt = a.Attempt,
        Status = a.Status,
        CreatedAt = a.CreatedAt,
        StartedAt = a.StartedAt,
    };

    public static Contract.StepFiles ToApi(AttemptFilesResponse f) => new()
    {
        Run = f.Run,
        Step = f.Step,
        Attempt = f.Attempt,
        Files = [.. f.Files.Select(e => new Contract.StepFile { Path = e.Path, Size = e.Size, LastModified = e.LastModified })],
    };

    public static Contract.CriticalPathReport ToApi(CriticalPathReport r) => new()
    {
        From = r.From,
        To = r.To,
        Runs = [.. r.Runs.Select(ToApi)],
        Steps = [.. r.Steps.Select(s => new Contract.CriticalPathStep
        {
            Step = s.Step,
            Runs = s.Runs,
            Seconds = s.Seconds,
            Share = s.Share,
            AverageSeconds = s.AverageSeconds,
            WaitSeconds = s.WaitSeconds,
            RunNumbers = s.RunNumbers,
            OverheadSeconds = s.OverheadSeconds,
        })],
        Skipped = [.. r.Skipped.Select(s => new Contract.CriticalPathSkip { Number = s.Number, Reason = s.Reason })],
        TotalSeconds = r.TotalSeconds,
        AverageSeconds = r.AverageSeconds,
    };

    private static Contract.CriticalPathRun ToApi(CriticalPathRun r) => new()
    {
        Number = r.Number,
        Status = PipelineViews.ToApi(r.Status),
        VersionNumber = r.VersionNumber,
        VersionStamp = r.VersionStamp,
        Seconds = r.Seconds,
        IdleSeconds = r.IdleSeconds,
        Path = [.. r.Path.Select(p => new Contract.CriticalPathStage
        {
            Stage = p.Stage,
            Step = p.Step,
            Attempts = p.Attempts,
            Seconds = p.Seconds,
            WaitSeconds = p.WaitSeconds,
            ActiveSeconds = p.ActiveSeconds,
        })],
        Notes = r.Notes,
        ActiveSeconds = r.ActiveSeconds,
        Lead = r.Lead is null ? null : new Contract.CriticalPathLead
        {
            Seconds = r.Lead.Seconds,
            Via = r.Lead.Via,
            Detect = r.Lead.Detect,
            Check = r.Lead.Check,
            Map = r.Lead.Map,
            Start = r.Lead.Start,
        },
    };
}
