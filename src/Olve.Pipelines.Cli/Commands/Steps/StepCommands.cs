using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands.Runs;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Steps;

/// <summary><c>pl step output &lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@n]</c> — an attempt's output JSON.</summary>
public sealed class StepOutputCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "output";
    public int RequiredOperands => 1;
    public string HelpLine => "Print a step's output (latest successful attempt, or @n)";
    public string? HelpDetail =>
        """
        pl step output <pipeline>#<run>/<step>[@<attempt>]

          Prints the output JSON on stdout (pipe it to jq). Without @attempt: the latest successful
          attempt — the one later stages consumed. --json prints the full response (run, step,
          attempt, status, output, outputKey).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if ((await ctx.Api.GetStepOutput(addr.Id, addr.Run, addr.Step, addr.Attempt, ct)).TryPickProblems(out var outputProblems, out var output))
            return outputProblems;

        ctx.Output.Emit(output, CliJsonContext.Default.AttemptOutputResponse, () =>
        {
            ctx.Log.Log($"{RunAddress.Format(addr.Label, output.Run, output.Step, output.Attempt)} ({output.Status})");
            ctx.Output.Line(output.Output is { } json ? Indented(json) : "null");
        });
        return Result.Success();
    }
}

/// <summary><c>pl step files &lt;address&gt;</c> — an attempt's stored files.</summary>
public sealed class StepFilesCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "files";
    public int RequiredOperands => 1;
    public string HelpLine => "List a step's stored output files";
    public string? HelpDetail =>
        """
        pl step files <pipeline>#<run>/<step>[@<attempt>]

          Lists the files the attempt stored (a directory ref `dir/` is stored as dir.tar).
          Download one with `pl step file <address> <path>`.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if ((await ctx.Api.ListStepFiles(addr.Id, addr.Run, addr.Step, addr.Attempt, ct)).TryPickProblems(out var listProblems, out var files))
            return listProblems;

        ctx.Output.Emit(files, CliJsonContext.Default.AttemptFilesResponse, () =>
        {
            ctx.Output.Line($"{RunAddress.Format(addr.Label, files.Run, files.Step, files.Attempt)}");
            if (files.Files.Length == 0)
            {
                ctx.Output.Line("(no files)");
                return;
            }

            var rows = files.Files.Select(f => (IReadOnlyList<string>)[f.Path, Size(f.Size), Time(f.LastModified)]).ToList();
            foreach (var line in Table.Render(["PATH", "SIZE", "MODIFIED"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GiB",
    };
}

/// <summary><c>pl step file &lt;address&gt; &lt;path&gt; [-o out]</c> — download one stored file.</summary>
public sealed class StepFileCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "file";
    public int RequiredOperands => 1;
    public IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["o"] = "output" };
    public string HelpLine => "Download one of a step's files (to stdout, or -o <file>)";
    public string? HelpDetail =>
        """
        pl step file <pipeline>#<run>/<step>[@<attempt>] <path> [-o <file>]
        pl step file <pipeline>#<run>/<step>[@<attempt>]/<path> [-o <file>]

          Writes the file to stdout, or to <file> with -o/--output. A directory ref `dir/`
          downloads as a tar.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct, allowPath: true)).TryPickProblems(out var problems, out var addr))
            return problems;

        var path = cli.Operand(1) ?? addr.Address.Path;
        if (path is null)
            return new ResultProblem("Which file? Pass a path: `pl step file <address> <path>` (list them with `pl step files`).");
        if (cli.Operand(1) is not null && addr.Address.Path is not null)
            return new ResultProblem("Give the file path once: in the address or as an argument, not both.");

        var outFile = cli.Option("output");
        if (outFile is null or "-")
        {
            await using var stdout = Console.OpenStandardOutput();
            var toStdout = await ctx.Api.DownloadStepFile(addr.Id, addr.Run, addr.Step, path, addr.Attempt, stdout, ct);
            return toStdout.TryPickProblems(out var stdoutProblems) ? stdoutProblems : Result.Success();
        }

        // Download to a temp sibling and move into place, so a failed download never leaves a partial file.
        var full = Path.GetFullPath(outFile);
        var temp = full + ".part";
        Result<long> result;
        try
        {
            await using (var file = File.Create(temp))
                result = await ctx.Api.DownloadStepFile(addr.Id, addr.Run, addr.Step, path, addr.Attempt, file, ct);
            if (result.TryPickProblems(out var downloadProblems, out var bytes))
            {
                File.Delete(temp);
                return downloadProblems;
            }

            File.Move(temp, full, overwrite: true);
            if (!ctx.Output.IsJson)
                ctx.Output.Line($"wrote {full} ({StepFilesCommand.Size(bytes)})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { }
            return new ResultProblem("Could not write '{0}': {1}", full, ex.Message);
        }

        return Result.Success();
    }
}

/// <summary><c>pl step logs &lt;address&gt; [--follow]</c>.</summary>
public sealed class StepLogsCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "logs";
    public int RequiredOperands => 1;
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "follow" };
    public IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["f"] = "follow" };
    public string HelpLine => "Print a step attempt's logs (latest attempt, or @n; --follow polls)";
    public string? HelpDetail =>
        """
        pl step logs <pipeline>#<run>/<step>[@<attempt>] [--follow] [--interval <seconds>]

          Without @attempt: the latest attempt, whatever its status. A running attempt shows the
          pod's current logs. --follow/-f polls until the attempt finishes (default every 2s).
        """;

    /// <summary>Poll delay seam for tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;

        if (!cli.HasFlag("follow"))
        {
            if ((await ctx.Api.GetStepLogs(addr.Id, addr.Run, addr.Step, addr.Attempt, ct)).TryPickProblems(out var logProblems, out var logs))
                return logProblems;
            ctx.Output.Write(logs.EndsWith('\n') || logs.Length == 0 ? logs : logs + "\n");
            return Result.Success();
        }

        if (!double.TryParse(cli.Option("interval", "2"), System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            return new ResultProblem("--interval must be a positive number of seconds.");

        return await FollowAsync(ctx, addr, TimeSpan.FromSeconds(seconds), ct);
    }

    private async Task<Result> FollowAsync(CommandContext ctx, ResolvedAddress addr, TimeSpan interval, CancellationToken ct)
    {
        var printed = "";
        try
        {
            while (true)
            {
                // Status first, then logs: once the attempt is seen finished, the fetch after it is complete.
                if ((await ctx.Api.ListStepAttempts(addr.Id, addr.Run, addr.Step, ct)).TryPickProblems(out var attemptProblems, out var attempts))
                    return attemptProblems;
                var target = addr.Attempt is { } n ? attempts.FirstOrDefault(a => a.Number == n) : attempts.LastOrDefault();
                var finished = target is not null && !IsActive(target);

                // No attempt yet: keep waiting only while the run can still reach the step.
                if (target is null)
                {
                    if ((await ctx.Api.GetRun(addr.Id, addr.Run, ct)).TryPickProblems(out var runProblems, out var detail))
                        return runProblems;
                    if (detail.Run.Status != "Running")
                        return new ResultProblem("Run #{0} is {1} and has no {2} of step '{3}'.", addr.Run, detail.Run.Status,
                            addr.Attempt is { } a ? $"attempt {a}" : "attempt", addr.Step);
                }

                if (target is not null)
                {
                    if ((await ctx.Api.GetStepLogs(addr.Id, addr.Run, addr.Step, target.Number, ct)).TryPickProblems(out var logProblems, out var logs))
                        return logProblems;

                    if (logs.StartsWith(printed, StringComparison.Ordinal))
                    {
                        ctx.Output.Write(logs[printed.Length..]);
                    }
                    else
                    {
                        // The source changed (e.g. init container -> step container, or stored log replaced the live one).
                        ctx.Output.Write((printed.Length > 0 && !printed.EndsWith('\n') ? "\n" : "") + logs);
                    }

                    printed = logs;
                }

                if (finished)
                {
                    if (printed.Length > 0 && !printed.EndsWith('\n'))
                        ctx.Output.Write("\n");
                    ctx.Log.Log($"attempt @{target!.Number} {target.Status.Kind}");
                    return Result.Success();
                }

                await Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Success(); // Ctrl+C stops following
        }
    }
}

/// <summary><c>pl step rerun &lt;address&gt; [--continue]</c> — a new attempt of one step.</summary>
public sealed class StepRerunCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "rerun";
    public int RequiredOperands => 1;
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "continue" };
    public string HelpLine => "Rerun one step as a new attempt (--continue re-opens the run downstream)";
    public string? HelpDetail =>
        """
        pl step rerun <pipeline>#<run>/<step> [--continue]

          Adds an attempt in the same run (same pinned version, current pool input). Rerun only by
          default; --continue also continues the run downstream of the step.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if (addr.Attempt is not null)
            return new ResultProblem("A rerun creates a new attempt; drop the @{0} from the address.", addr.Attempt);

        var cont = cli.HasFlag("continue");
        if ((await ctx.Api.RerunStep(addr.Id, addr.Run, addr.Step, cont, ct)).TryPickProblems(out var rerunProblems, out var attempt))
            return rerunProblems;

        ctx.Output.Emit(attempt, CliJsonContext.Default.Job, () =>
            ctx.Output.Line($"{RunAddress.Format(addr.Label, attempt.RunNumber, attempt.StepName, attempt.Number)}  {attempt.Status.Kind}{(cont ? " (run continues downstream)" : "")}"));
        return Result.Success();
    }
}

/// <summary><c>pl step history &lt;pipeline&gt;/&lt;step&gt;</c> — a step identity's attempts across runs.</summary>
public sealed class StepHistoryCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "history";
    public int RequiredOperands => 1;
    public string HelpLine => "A step's history across runs, with definition-change markers";
    public string? HelpDetail =>
        """
        pl step history <pipeline>/<step> [--limit <n>]

          Newest first. A `*` marks the first row after the step's definition changed (its step
          version differs from the row below).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        int? limit = null;
        if (cli.Option("limit") is { } limitText)
        {
            if (!int.TryParse(limitText, out var l) || l < 1)
                return new ResultProblem("--limit must be a positive integer.");
            limit = l;
        }

        if (StepRef.Parse(cli.Operand(0)!).TryPickProblems(out var refProblems, out var stepRef))
            return refProblems;
        if ((await ResolvePipelineAsync(ctx, stepRef.Pipeline, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.GetStepHistory(pipeline.Id.ToString(), stepRef.Step, limit, ct)).TryPickProblems(out var historyProblems, out var items))
            return historyProblems;

        ctx.Output.Emit(items, CliJsonContext.Default.StepHistoryItemArray, () =>
        {
            if (items.Length == 0)
            {
                ctx.Output.Line($"(no attempts of '{stepRef.Step}')");
                return;
            }

            var rows = items.Select(i => (IReadOnlyList<string>)
            [
                $"{(i.StepVersionChanged ? "*" : " ")} #{i.RunNumber}@{i.AttemptNumber}", i.Status, Duration(i.StartedAt, i.FinishedAt),
                i.StepVersion is { } v ? $"{stepRef.Step}.{v}" : "-", $"v{i.VersionNumber}", Time(i.CreatedAt), i.Reason ?? "",
            ]).ToList();
            foreach (var line in Table.Render(["  ATTEMPT", "STATUS", "TOOK", "STEP VERSION", "PIPELINE", "CREATED", "REASON"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}
