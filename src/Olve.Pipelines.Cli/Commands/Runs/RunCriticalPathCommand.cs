using System.Globalization;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Runs;

/// <summary><c>pl run critical-path &lt;pipeline&gt; [--from N] [--to M] [--step X] [--all]</c> — where a range of runs spent its time.</summary>
public sealed class RunCriticalPathCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("from", "to", "step");
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "all" };
    public string Noun => "run";
    public string Verb => "critical-path";
    public int RequiredOperands => 1;
    public string HelpLine => "The critical path of a range of runs: each step's time on it and share of it";
    public string? HelpDetail =>
        """
        pl run critical-path <pipeline> [--from <run>] [--to <run>] [--step <name>] [--all]

          Stages are barriers, so each stage's critical step is the one that finished last; it is
          charged from the stage's first attempt to that finish (WAIT: how much of it was spent
          before the step's first attempt started). Automatic retries count, later reruns don't.

          --from, --to   Run numbers (default: the latest 20 runs; at most 500)
          --step <name>  Also list the runs that step was on the critical path in
          --all          Include failed and cancelled runs (default: succeeded only)
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (Number(cli, "from").TryPickProblems(out var fromProblems, out var from))
            return fromProblems;
        if (Number(cli, "to").TryPickProblems(out var toProblems, out var to))
            return toProblems;

        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.GetCriticalPath(pipeline.Id.ToString(), from, to, cli.HasFlag("all"), ct)).TryPickProblems(out var reportProblems, out var report))
            return reportProblems;

        var step = cli.Option("step");
        ctx.Output.Emit(report, CliJsonContext.Default.CriticalPathReport, () =>
        {
            if (report.Runs.Length == 0)
            {
                ctx.Output.Line($"(no runs to report in #{report.From}..#{report.To})");
                foreach (var skip in report.Skipped)
                    ctx.Output.Line($"#{skip.Number}: {skip.Reason}");
                return;
            }

            var runRows = report.Runs.Select(r => (IReadOnlyList<string>)
            [
                $"#{r.Number}", r.Status, Seconds(r.Seconds), r.IdleSeconds >= 1 ? Seconds(r.IdleSeconds) : "-",
                string.Join(" → ", r.Path.Select(p => $"{p.Step}{(p.Attempts > 1 ? $"×{p.Attempts}" : "")} {Seconds(p.Seconds)}")),
            ]).ToList();
            foreach (var line in Table.Render(["RUN", "STATUS", "PATH", "IDLE", "CRITICAL PATH"], runRows))
                ctx.Output.Line(line);

            ctx.Output.Line();
            ctx.Output.Line($"{report.Runs.Length} run(s) in #{report.From}..#{report.To}: {Seconds(report.TotalSeconds)} on the critical path, {Seconds(report.AverageSeconds)} per run");
            ctx.Output.Line();

            var stepRows = report.Steps.Select(s => (IReadOnlyList<string>)
            [
                s.Step, $"{s.Runs}/{report.Runs.Length}", Seconds(s.Seconds), (s.Share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                Seconds(s.AverageSeconds), Seconds(s.WaitSeconds),
            ]).ToList();
            foreach (var line in Table.Render(["STEP", "ON PATH", "TOTAL", "SHARE", "AVG WHEN ON", "WAIT"], stepRows))
                ctx.Output.Line(line);

            var notes = report.Runs.SelectMany(r => r.Notes.Select(n => $"#{r.Number}: {n}"))
                .Concat(report.Skipped.Select(s => $"#{s.Number}: not reported ({s.Reason})")).ToList();
            if (notes.Count > 0)
            {
                ctx.Output.Line();
                foreach (var note in notes)
                    ctx.Output.Line(note);
            }

            if (step is not null)
            {
                var runs = report.Steps.FirstOrDefault(s => s.Step == step)?.RunNumbers ?? [];
                var seconds = report.Runs.ToDictionary(r => r.Number, r => r.Path.Where(p => p.Step == step).Sum(p => p.Seconds));
                ctx.Output.Line();
                ctx.Output.Line($"{step} was on the critical path in {runs.Length}/{report.Runs.Length} run(s)"
                                + (runs.Length > 0 ? ": " + string.Join(", ", runs.Select(n => $"#{n} ({Seconds(seconds[n])})")) : ""));
            }
        });
        return Result.Success();
    }

    private static Result<int?> Number(CliArgs cli, string option)
    {
        if (cli.Option(option) is not { } text)
            return (int?)null;
        return int.TryParse(text.TrimStart('#'), out var n) && n > 0 ? n : new ResultProblem("--{0} must be a run number.", option);
    }

    private static string Seconds(double seconds) => Duration(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(Math.Max(0, seconds)));
}
