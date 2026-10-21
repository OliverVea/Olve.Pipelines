using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Runs;

/// <summary><c>pl trigger &lt;pipeline&gt; [--input json|@file] [--field k=v…]</c> — start a manual run.</summary>
public sealed class TriggerCommand : ICliCommand
{
    public string Noun => "trigger";
    public string Verb => "";
    public int RequiredOperands => 1;
    public string HelpLine => "Start a manual run with an input; prints the run address";
    public string? HelpDetail =>
        """
        pl trigger <pipeline> [--input <json|@file|@->] [--field k=v ...]

          Starts a run on the pipeline's current version. The body is the pipeline input: --input
          gives a whole JSON object (inline, @file, or @- for stdin) and each --field sets one
          top-level field over it, typed by the version's input model (sha=0123 stays a string).
          The service fills defaults and validates; problems are listed, no run is started.
          <pipeline> is a pipeline id or name.

          Prints the run address (<pipeline>#<run>) — use it with `pl run get` / `pl step ...`.

          Examples:
            pl trigger olve-pipelines --field deployTo=beta --field sha=65bdc91
            pl trigger olve-pipelines --input '{"deployTo":"prod","sha":"65bdc91"}'
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;

        var fields = cli.Options("field");
        var schema = fields.Count > 0 ? await InputSchemaAsync(ctx, pipeline.Id.ToString(), null, ct) : null;
        if (BuildInput(cli.Option("input"), fields, schema).TryPickProblems(out var inputProblems, out var input))
            return inputProblems;

        if ((await ctx.Api.TriggerRun(pipeline.Id.ToString(), input.ToJsonString(), ct)).TryPickProblems(out var triggerProblems, out var response))
            return triggerProblems;

        ctx.Output.Emit(response, CliJsonContext.Default.TriggerRunResponse, () => PrintOutcome(ctx.Output, pipeline.Label, response));
        return Result.Success();
    }

    public static void PrintOutcome(IOutputWriter output, string label, TriggerRunResponse response)
    {
        switch (response.Outcome, response.Run)
        {
            case ("started", { } run):
                output.Line(RunAddress.Format(label, run.Number));
                output.Line($"started run #{run.Number} on v{run.VersionNumber} ({run.VersionStamp})");
                break;
            case ("deduplicated", { } run):
                output.Line(RunAddress.Format(label, run.Number));
                output.Line($"deduplicated: run #{run.Number} already has this input ({run.Status})");
                break;
            case ("queued", _):
                output.Line("queued: the pipeline is switching versions; the run starts when the switch completes");
                break;
            default:
                output.Line(response.Outcome);
                break;
        }
    }
}

/// <summary><c>pl run list &lt;pipeline&gt;</c> — runs, newest first.</summary>
public sealed class RunListCommand : ICliCommand
{
    public string Noun => "run";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List a pipeline's runs (newest first)";
    public string? HelpDetail =>
        """
        pl run list <pipeline> [--page <n>] [--page-size <n>]

          --page <n>        Page number (default: 0)
          --page-size <n>   Page size (default: 20)
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (!int.TryParse(cli.Option("page", "0"), out var page) || page < 0)
            return new ResultProblem("--page must be a non-negative integer.");
        if (!int.TryParse(cli.Option("page-size", "20"), out var pageSize) || pageSize < 1)
            return new ResultProblem("--page-size must be a positive integer.");

        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.ListRuns(pipeline.Id.ToString(), page, pageSize, ct)).TryPickProblems(out var listProblems, out var runs))
            return listProblems;

        ctx.Output.Emit(runs, CliJsonContext.Default.PageOfRunSummary, () =>
        {
            var rows = runs.Items.Select(r => (IReadOnlyList<string>)
                [$"#{r.Number}", r.Status, r.TriggerKind, $"v{r.VersionNumber}", Time(r.CreatedAt), Duration(r.CreatedAt, r.FinishedAt), Note(r)]).ToList();
            foreach (var line in Table.Render(["RUN", "STATUS", "TRIGGER", "VERSION", "CREATED", "TOOK", "NOTE"], rows))
                ctx.Output.Line(line);
            ctx.Output.Line();
            ctx.Output.Line($"page {runs.PageNumber} · {runs.Items.Length} of {runs.TotalCount} run(s)");
        });
        return Result.Success();
    }

    public static string Note(RunSummary r)
    {
        var notes = new List<string>();
        if (r.HeldAt is { } held)
            notes.Add($"held at {held}");
        if (r.SupersededByNumber is { } by)
            notes.Add($"superseded by #{by}");
        if (r.RerunOfNumber is { } of)
            notes.Add($"rerun of #{of}");
        return string.Join("; ", notes);
    }
}

/// <summary><c>pl run get &lt;pipeline&gt;#&lt;run&gt;</c> — input, pinned version and the stage layout with per-step status.</summary>
public sealed class RunGetCommand : ICliCommand
{
    public string Noun => "run";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Show a run: input, version, stages and each step's status";
    public string? HelpDetail =>
        """
        pl run get <pipeline>#<run>

          Shows the run's input, pinned version + commit, links (superseded/rerun), whether it can
          still be continued, and the pinned plan's stages (with `when` conditions) listing each
          step's latest attempt: status, attempt, duration, skip/failure reason, gate (blocked/held).
          Step statuses without an attempt: held (a blocked gate holds the run here), pending
          (not reached yet), not-run (the run finished without it).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveRunAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if ((await ctx.Api.GetRun(addr.Id, addr.Run, ct)).TryPickProblems(out var getProblems, out var detail))
            return getProblems;

        ctx.Output.Emit(detail, CliJsonContext.Default.RunDetail, () => Print(ctx.Output, addr.Label, detail));
        return Result.Success();
    }

    public static void Print(IOutputWriter o, string label, RunDetail d)
    {
        var r = d.Run;
        o.Line($"{RunAddress.Format(label, r.Number)}  {r.Status}");
        o.Line($"version:   v{r.VersionNumber} · {r.VersionStamp} · commit {Short(d.CommitSha)}");
        o.Line($"trigger:   {r.TriggerKind} · created {Time(r.CreatedAt)} · finished {Time(r.FinishedAt)} · took {Duration(r.CreatedAt, r.FinishedAt)}");
        o.Line($"input:     {Json(d.Input)}");
        if (RunListCommand.Note(r) is { Length: > 0 } note)
            o.Line($"note:      {note}");
        o.Line($"continue:  {(d.Continuable ? "possible" : "no (not on the active version, or superseded)")}");

        if (d.Stages.Length == 0)
        {
            o.Line();
            o.Line("(stage layout unavailable: the pinned version's manifest could not be read)");
            return;
        }

        foreach (var stage in d.Stages)
        {
            o.Line();
            var when = stage.When.Length == 0
                ? ""
                : "  when " + string.Join(" and ", stage.When.Select(c => $"{c.Field} in [{string.Join(", ", c.Values.Select(Json))}]"));
            o.Line($"stage {stage.Index + 1}{when}");
            var rows = stage.Steps.Select(s => (IReadOnlyList<string>)
                ["  " + s.Name, s.Status, s.LatestAttempt is { } n ? $"@{n}/{s.AttemptCount}" : "-", Duration(s.StartedAt, s.FinishedAt), StepNote(s)]).ToList();
            foreach (var line in Table.Render(["  STEP", "STATUS", "ATTEMPT", "TOOK", "NOTE"], rows))
                o.Line(line);
        }
    }

    private static string StepNote(RunStepView s)
    {
        var notes = new List<string>();
        if (s.Blocked)
            notes.Add("gate blocked");
        if (s.SucceededAttempt is { } ok && ok != s.LatestAttempt)
            notes.Add($"uses @{ok}");
        if (s.Reason is { Length: > 0 } reason)
            notes.Add(reason);
        return string.Join("; ", notes);
    }
}

/// <summary><c>pl run cancel &lt;pipeline&gt;#&lt;run&gt;</c>.</summary>
public sealed class RunCancelCommand : ICliCommand
{
    public string Noun => "run";
    public string Verb => "cancel";
    public int RequiredOperands => 1;
    public string HelpLine => "Cancel a run (also its scheduled and in-progress attempts)";
    public string? HelpDetail => "pl run cancel <pipeline>#<run>   Cancels the run; in-progress attempts' Jobs are deleted.";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveRunAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if ((await ctx.Api.CancelRun(addr.Id, addr.Run, ct)).TryPickProblems(out var cancelProblems, out var run))
            return cancelProblems;

        ctx.Output.Emit(run, CliJsonContext.Default.Run, () => ctx.Output.Line($"{RunAddress.Format(addr.Label, run.Number)}  {run.Status}"));
        return Result.Success();
    }
}

/// <summary><c>pl run rerun &lt;pipeline&gt;#&lt;run&gt;</c> — a new run with the same input, linked to the original.</summary>
public sealed class RunRerunCommand : ICliCommand
{
    public string Noun => "run";
    public string Verb => "rerun";
    public int RequiredOperands => 1;
    public string HelpLine => "Rerun a whole run: a new run with the same input (prints its address)";
    public string? HelpDetail => "pl run rerun <pipeline>#<run>   A new run with the same input on the current version, linked via rerunOf.";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveRunAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if ((await ctx.Api.RerunRun(addr.Id, addr.Run, ct)).TryPickProblems(out var rerunProblems, out var response))
            return rerunProblems;

        ctx.Output.Emit(response, CliJsonContext.Default.TriggerRunResponse, () => TriggerCommand.PrintOutcome(ctx.Output, addr.Label, response));
        return Result.Success();
    }
}

/// <summary><c>pl run continue &lt;pipeline&gt;#&lt;run&gt; [--field k=v…]</c> — keep completed stages, re-evaluate the rest.</summary>
public sealed class RunContinueCommand : ICliCommand
{
    public string Noun => "run";
    public string Verb => "continue";
    public int RequiredOperands => 1;
    public string HelpLine => "Continue a run, optionally changing input fields (promote what you tested)";
    public string? HelpDetail =>
        """
        pl run continue <pipeline>#<run> [--field k=v ...] [--input <json|@file|@->]

          Keeps the completed stages and re-evaluates `when` for the rest. Changed fields are merged
          over the run's stored input and validated against its pinned input model; a field a
          completed step already consumed cannot change (e.g. sha).

          Example: pl run continue olve-pipelines#412 --field deployTo=prod
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveRunAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;

        string? changes = null;
        var fields = cli.Options("field");
        if (fields.Count > 0 || cli.Option("input") is not null)
        {
            // Type --field values by the run's *pinned* input model.
            System.Text.Json.Nodes.JsonObject? schema = null;
            if (fields.Count > 0 && !(await ctx.Api.GetRun(addr.Id, addr.Run, ct)).TryPickProblems(out _, out var detail))
                schema = await InputSchemaAsync(ctx, addr.Id, detail.Run.VersionNumber, ct);
            if (BuildInput(cli.Option("input"), fields, schema).TryPickProblems(out var inputProblems, out var input))
                return inputProblems;
            changes = input.ToJsonString();
        }

        if ((await ctx.Api.ContinueRun(addr.Id, addr.Run, changes, ct)).TryPickProblems(out var continueProblems, out var run))
            return continueProblems;

        ctx.Output.Emit(run, CliJsonContext.Default.Run, () =>
            ctx.Output.Line($"{RunAddress.Format(addr.Label, run.Number)}  {run.Status} (input {run.Input})"));
        return Result.Success();
    }
}
