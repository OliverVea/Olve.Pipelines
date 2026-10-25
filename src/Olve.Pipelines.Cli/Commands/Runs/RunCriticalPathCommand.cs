using System.Globalization;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Runs;

/// <summary><c>pl run critical-path &lt;pipeline&gt; [--from N] [--to M] [--step X] [--all]</c> — where a range of runs spent its time.</summary>
public sealed class RunCriticalPathCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("from", "to", "step", "since-version");
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "all" };
    public string Noun => "run";
    public string Verb => "critical-path";
    public int RequiredOperands => 1;
    public string HelpLine => "The critical path of a range of runs: each step's time on it and share of it";
    public string? HelpDetail =>
        """
        pl run critical-path <pipeline> [--from <run> | --since-version <v>] [--to <run>] [--step <name>] [--all]

          Stages are barriers, so each stage's critical step is the one that finished last; it is
          charged from the stage's first attempt to that finish (WAIT: how much of it was spent
          before the step's first attempt started). Automatic retries count, later reruns don't.

          --from, --to   Run numbers (default: the latest 20 runs; at most 500)
          --since-version <v>  From the first run on pipeline version v or later (instead of --from)
          --step <name>  Also list the runs that step was on the critical path in
          --all          Include failed and cancelled runs (default: succeeded only)

          LEAD is the time from the push (or from when the server noticed it) to the first step:
          detect (push to webhook or poll), check (branch head and config applied, a reconcile
          included), map (the map-trigger Job) and start. Automatic runs only.

          AVG OVERHEAD is the average part of a step's time on the path outside its step container
          once it was picked up (pod start, fetching, image pull, noticing the end, collecting the
          result; WAIT is separate), over the runs that recorded it; "-" when none did.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (Number(cli, "from").TryPickProblems(out var fromProblems, out var from))
            return fromProblems;
        if (Number(cli, "to").TryPickProblems(out var toProblems, out var to))
            return toProblems;
        if (Number(cli, "since-version").TryPickProblems(out var versionProblems, out var sinceVersion))
            return versionProblems;

        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.GetCriticalPath(pipeline.Id.ToString(), from, to, cli.HasFlag("all"), sinceVersion, ct)).TryPickProblems(out var reportProblems, out var report))
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
                $"#{r.Number}", r.Status, r.Lead is { } lead ? Seconds(lead.Seconds) : "-", Seconds(r.Seconds), r.IdleSeconds >= 1 ? Seconds(r.IdleSeconds) : "-",
                string.Join(" → ", r.Path.Select(p => $"{p.Step}{(p.Attempts > 1 ? $"×{p.Attempts}" : "")} {Seconds(p.Seconds)}")),
            ]).ToList();
            foreach (var line in Table.Render(["RUN", "STATUS", "LEAD", "PATH", "IDLE", "CRITICAL PATH"], runRows))
                ctx.Output.Line(line);

            ctx.Output.Line();
            var known = report.Runs.Where(r => r.ActiveSeconds is not null).ToList();
            var overhead = known.Count > 0
                ? $"; overhead {Seconds(known.Sum(r => r.Seconds - r.Path.Sum(p => p.WaitSeconds) - r.ActiveSeconds!.Value) / known.Count)} per run ({known.Count} with timings)"
                : "";
            ctx.Output.Line($"{report.Runs.Length} run(s) in #{report.From}..#{report.To}: {Seconds(report.TotalSeconds)} on the critical path, {Seconds(report.AverageSeconds)} per run{overhead}");
            var leads = report.Runs.Where(r => r.Lead is not null).Select(r => r.Lead!).ToList();
            if (leads.Count > 0)
                ctx.Output.Line($"lead (push to first step), average of {leads.Count}: {Seconds(leads.Average(l => l.Seconds))} = detect {Part(leads, l => l.Detect)} + check {Part(leads, l => l.Check)} + map {Part(leads, l => l.Map)} + start {Part(leads, l => l.Start)}");
            ctx.Output.Line();

            var stepRows = report.Steps.Select(s => (IReadOnlyList<string>)
            [
                s.Step, $"{s.Runs}/{report.Runs.Length}", Seconds(s.Seconds), (s.Share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                Seconds(s.AverageSeconds), Seconds(s.WaitSeconds),
                s.OverheadSeconds is { } overhead ? Seconds(overhead) : "-",
            ]).ToList();
            foreach (var line in Table.Render(["STEP", "ON PATH", "TOTAL", "SHARE", "AVG WHEN ON", "WAIT", "AVG OVERHEAD"], stepRows))
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
        return int.TryParse(text.TrimStart('#', 'v'), out var n) && n > 0 ? n : new ResultProblem("--{0} must be a positive number.", option);
    }

    private static string Part(List<CriticalPathLead> leads, Func<CriticalPathLead, double?> part)
    {
        var known = leads.Select(part).Where(v => v is not null).Select(v => v!.Value).ToList();
        return known.Count > 0 ? Seconds(known.Average()) : "?";
    }

    private static string Seconds(double seconds) => Duration(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(Math.Max(0, seconds)));
}
