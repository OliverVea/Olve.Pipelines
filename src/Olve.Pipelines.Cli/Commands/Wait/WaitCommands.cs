using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Wait;

/// <summary>One look at what a wait is waiting for.</summary>
internal abstract record WaitCheck
{
    /// <summary>Not yet; <see cref="Progress"/> is printed to stderr when it changes.</summary>
    public sealed record Pending(string Progress) : WaitCheck;

    /// <summary>The condition holds: print <see cref="Output"/>; <see cref="Success"/> false exits 1 (e.g. the run failed).</summary>
    public sealed record Done(string Output, bool Success) : WaitCheck;
}

/// <summary>
/// The loop behind <c>pl wait</c>: check over REST, then re-check whenever a relevant event arrives on the
/// pipeline's <c>/api/events</c> stream (or every minute, in case the stream stalls). Events only wake the
/// check; REST decides. A retryable failure (the server restarting, a 5xx) is retried with backoff; any other
/// failure ends the wait. Exits 124 on <c>--timeout</c>.
/// </summary>
internal static class Waiter
{
    public const int TimeoutExitCode = 124;
    private static readonly TimeSpan Fallback = TimeSpan.FromMinutes(1);

    internal static TimeSpan RetryBackoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));

    public static async Task<Result> RunAsync(
        CliArgs cli, CommandContext ctx, string pipelineId, IReadOnlySet<string> wakeOn,
        Func<CancellationToken, Task<Result<WaitCheck>>> check, CancellationToken ct,
        Func<int, TimeSpan>? backoff = null)
    {
        backoff ??= RetryBackoff;
        if (ParseTimeout(cli.Option("timeout")).TryPickProblems(out var timeoutProblems, out var timeout))
            return timeoutProblems;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
            deadline.CancelAfter(t);

        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var watch = WatchAsync(ctx, pipelineId, wakeOn, wake.Writer, deadline.Token);
        string? lastProgress = null;
        var failures = 0;
        try
        {
            while (true)
            {
                var wait = Fallback;
                var checkResult = await check(deadline.Token);
                if (checkResult.TryPickProblems(out var problems, out var state))
                {
                    if (!problems.IsRetryable)
                        return problems;
                    wait = backoff(++failures);
                    state = new WaitCheck.Pending($"server unavailable ({problems.First()}); retrying");
                }
                else
                {
                    failures = 0;
                }

                switch (state)
                {
                    case WaitCheck.Done done:
                        ctx.Output.Line(done.Output);
                        if (!done.Success)
                            ctx.ExitCode = 1;
                        return Result.Success();
                    case WaitCheck.Pending pending when pending.Progress != lastProgress:
                        Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss}  {pending.Progress}");
                        lastProgress = pending.Progress;
                        break;
                }

                using var tick = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                tick.CancelAfter(wait);
                try
                {
                    await wake.Reader.ReadAsync(tick.Token);
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Timed out after {cli.Option("timeout")}{(lastProgress is null ? "" : $" ({lastProgress})")}.");
            ctx.ExitCode = TimeoutExitCode;
            return Result.Success();
        }
        finally
        {
            await deadline.CancelAsync();
            await watch;
        }
    }

    /// <summary>Keeps a subscription open (reconnecting when the stream ends) and signals on relevant events.</summary>
    private static async Task WatchAsync(CommandContext ctx, string pipelineId, IReadOnlySet<string> wakeOn, ChannelWriter<bool> wake, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var streamed = await ctx.Api.WatchEvents(pipelineId, (type, _) =>
                {
                    if (type == "resync" || wakeOn.Contains(type))
                        wake.TryWrite(true);
                    return Task.CompletedTask;
                }, ct);
                if (streamed.TryPickProblems(out var problems))
                    ctx.Log.Log($"event stream: {string.Join("; ", problems.Select(p => p.ToString()))}");
                wake.TryWrite(true); // reconnected: something may have changed meanwhile
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ctx.Log.Log($"event stream: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    internal static Result<TimeSpan?> ParseTimeout(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (TimeSpan?)null;
        var unit = char.IsLetter(text[^1]) ? text[^1] : 's';
        var digits = char.IsLetter(text[^1]) ? text[..^1] : text;
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return new ResultProblem("Invalid --timeout '{0}' (e.g. 90s, 30m, 2h).", text);
        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ => new ResultProblem("Invalid --timeout '{0}' (e.g. 90s, 30m, 2h).", text),
        };
    }

    /// <summary>
    /// The run <paramref name="number"/> points at, following <c>supersededBy</c> when <paramref name="follow"/>:
    /// a superseded run's work continues in the newer run (for pushes, a newer commit containing this one).
    /// Moves <paramref name="number"/> along the chain.
    /// </summary>
    internal static async Task<Result<RunDetail>> GetRunFollowingAsync(
        CommandContext ctx, string pipelineId, string label, RunCursor number, bool follow, CancellationToken ct)
    {
        while (true)
        {
            if ((await ctx.Api.GetRun(pipelineId, number.Value, ct)).TryPickProblems(out var problems, out var detail))
                return problems;
            if (!follow || detail.Run.Status != "Superseded" || detail.Run.SupersededByNumber is not { } next)
                return detail;
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss}  {RunAddress.Format(label, number.Value)} superseded by #{next}, following");
            number.Value = next;
        }
    }

    internal static bool IsTerminal(string runStatus) => runStatus is "Succeeded" or "Failed" or "Cancelled" or "Superseded";

    internal static WaitCheck RunFinished(string label, RunSummary run) => IsTerminal(run.Status)
        ? new WaitCheck.Done($"{RunAddress.Format(label, run.Number)}  {run.Status}", run.Status == "Succeeded")
        : new WaitCheck.Pending($"{RunAddress.Format(label, run.Number)}: {run.Status}");
}

internal sealed class RunCursor(int value)
{
    public int Value { get; set; } = value;
}

/// <summary><c>pl wait run &lt;address&gt;</c> — wait for a run (or one step of it) to finish, start or be held.</summary>
public sealed class WaitRunCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("until", "timeout");
    public string Noun => "wait";
    public string Verb => "run";
    public int RequiredOperands => 1;
    public string HelpLine => "Wait for a run or step to finish (or --until started/held); exit 0 on success";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "no-follow" };
    public string? HelpDetail =>
        """
        pl wait run <pipeline>#<run>[/<step>] [--until finished|started|held] [--timeout 45m] [--no-follow]

          Blocks until the run (or the step) reaches the state, then prints it. Re-checks on the
          pipeline's live events, and every minute in case the stream stalls. A superseded run is
          followed to the run that superseded it (printed on stderr); the result is that run's.

          --until finished  (default) run: Succeeded → exit 0; Failed or Cancelled → 1.
                            step: succeeded, skipped or excluded → 0; failed or cancelled, or the run
                            ended without running it → 1.
          --until started   the step has an attempt (or the run exists).
          --until held      the run is held at a blocked step; exits 1 if it finishes first.
          --timeout <dur>   give up after 90s / 30m / 2h: exit 124.
          --no-follow       stop at a superseded run instead: exit 1.

          Progress goes to stderr, the result to stdout.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (RunAddress.Parse(cli.Operand(0)!).TryPickProblems(out var parseProblems, out var address))
            return parseProblems;
        if ((await ResolvePipelineAsync(ctx, address.Pipeline, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        var until = cli.Option("until", "finished");
        if (until is not ("finished" or "started" or "held"))
            return new ResultProblem("--until must be finished, started or held, got '{0}'.", until);

        var id = pipeline.Id.ToString();
        var number = new RunCursor(address.Run);
        var follow = !cli.HasFlag("no-follow");
        return await Waiter.RunAsync(cli, ctx, id, new HashSet<string> { "run", "attempt" }, async c =>
        {
            if ((await Waiter.GetRunFollowingAsync(ctx, id, address.Pipeline, number, follow, c)).TryPickProblems(out var getProblems, out var detail))
                return getProblems;
            return address.Step is { } step
                ? StepState(address.Pipeline, detail, step, until)
                : RunState(address.Pipeline, detail.Run, until);
        }, ct);
    }

    private static Result<WaitCheck> RunState(string label, RunSummary run, string until) => until switch
    {
        "started" => new WaitCheck.Done($"{RunAddress.Format(label, run.Number)}  {run.Status}", true),
        "held" when run.HeldAt is { } heldAt => new WaitCheck.Done($"{RunAddress.Format(label, run.Number)}  held at {heldAt}", true),
        "held" when Waiter.IsTerminal(run.Status) => new WaitCheck.Done($"{RunAddress.Format(label, run.Number)}  {run.Status} (never held)", false),
        "held" => new WaitCheck.Pending($"{RunAddress.Format(label, run.Number)}: {run.Status}, not held"),
        _ => Waiter.RunFinished(label, run),
    };

    private static Result<WaitCheck> StepState(string label, RunDetail detail, string step, string until)
    {
        var view = detail.Stages.SelectMany(s => s.Steps).FirstOrDefault(s => s.Name == step);
        if (view is null)
            return new ResultProblem("Run #{0} has no step '{1}'.", detail.Run.Number, step);

        var name = $"{RunAddress.Format(label, detail.Run.Number)}/{step}";
        var line = $"{name}  {view.Status}{(view.Reason is { } r ? $" ({r})" : "")}";
        var settled = view.Status is "succeeded" or "skipped" or "excluded" or "failed" or "cancelled" or "obsolete";
        if (until == "started")
            return view.AttemptCount > 0 || settled
                ? new WaitCheck.Done(line, view.Status is not ("failed" or "cancelled"))
                : RunEnded(detail, line) ?? new WaitCheck.Pending($"{name}: {view.Status}");
        if (until == "held")
            return view.Status == "held"
                ? new WaitCheck.Done(line, true)
                : settled ? new WaitCheck.Done($"{line} (never held)", false) : RunEnded(detail, line) ?? new WaitCheck.Pending($"{name}: {view.Status}");
        if (settled)
            return new WaitCheck.Done(line, view.Status is "succeeded" or "skipped" or "excluded");
        return RunEnded(detail, line) ?? new WaitCheck.Pending($"{name}: {view.Status}");
    }

    /// <summary>The run finished without settling the step: the wait can never succeed.</summary>
    private static WaitCheck? RunEnded(RunDetail detail, string line) =>
        Waiter.IsTerminal(detail.Run.Status) ? new WaitCheck.Done($"{line} (run {detail.Run.Status})", false) : null;
}

/// <summary><c>pl wait push &lt;pipeline&gt; &lt;sha&gt;</c> — wait for a run of a commit to appear (and finish).</summary>
public sealed class WaitPushCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("until", "timeout");
    private const int Window = 15;

    public string Noun => "wait";
    public string Verb => "push";
    public int RequiredOperands => 2;
    public string HelpLine => "Wait for a run of a commit to start (or --until finished); prints its address";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "no-follow" };
    public string? HelpDetail =>
        """
        pl wait push <pipeline> <sha> [--until started|finished] [--timeout 30m] [--no-follow]

          Blocks until a run whose input sha is <sha> (a prefix is enough) exists, and prints its
          address (e.g. olve-pipelines#412): addr=$(pl wait push olve-pipelines 6881313).
          --until finished keeps waiting until that run finishes (exit as `pl wait run`). When a
          newer push supersedes it, the wait follows the newer run, which contains the commit;
          --no-follow stops there instead (exit 1).
          Exits 1 when the binding's reconcile of that commit failed (no run will start).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        var sha = cli.Operand(1)!;
        var until = cli.Option("until", "started");
        if (until is not ("started" or "finished"))
            return new ResultProblem("--until must be started or finished, got '{0}'.", until);

        var id = pipeline.Id.ToString();
        var label = pipeline.Label;
        var notIt = new HashSet<int>();
        RunCursor? found = null;
        var follow = !cli.HasFlag("no-follow");
        return await Waiter.RunAsync(cli, ctx, id, new HashSet<string> { "run", "binding" }, async c =>
        {
            if (found is not null)
            {
                if ((await Waiter.GetRunFollowingAsync(ctx, id, label, found, follow, c)).TryPickProblems(out var getProblems, out var detail))
                    return getProblems;
                return Waiter.RunFinished(label, detail.Run);
            }

            if ((await ctx.Api.ListRuns(id, 0, Window, c)).TryPickProblems(out var listProblems, out var page))
                return listProblems;
            foreach (var run in page.Items.Where(r => !notIt.Contains(r.Number)))
            {
                if ((await ctx.Api.GetRun(id, run.Number, c)).TryPickProblems(out var getProblems, out var detail))
                    return getProblems;
                if (!InputSha(detail).StartsWith(sha, StringComparison.OrdinalIgnoreCase))
                {
                    notIt.Add(run.Number);
                    continue;
                }

                if (until == "started")
                    return new WaitCheck.Done(RunAddress.Format(label, run.Number), true);
                found = new RunCursor(run.Number);
                Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss}  {RunAddress.Format(label, run.Number)} is the run of {sha}");
                if (follow && detail.Run.Status == "Superseded"
                    && (await Waiter.GetRunFollowingAsync(ctx, id, label, found, follow, c)).TryPickProblems(out var followProblems, out detail))
                    return followProblems;
                return Waiter.RunFinished(label, detail.Run);
            }

            if ((await ctx.Api.GetPipelineBindingStatus(id, c)).TryPickProblems(out _, out var binding))
                return new WaitCheck.Pending($"no run of {sha} yet");
            if (binding.Result == ReconcileResult.Error && binding.ReconcileSha?.StartsWith(sha, StringComparison.OrdinalIgnoreCase) == true)
                return new WaitCheck.Done($"reconcile of {sha} failed: {string.Join("; ", binding.Problems)}", false);
            return new WaitCheck.Pending(binding.ReconcileInProgress ? $"no run of {sha} yet (reconciling)" : $"no run of {sha} yet");
        }, ct);
    }

    private static string InputSha(RunDetail detail) =>
        detail.Input.ValueKind == JsonValueKind.Object && detail.Input.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()!
            : "";
}

/// <summary><c>pl wait reconcile &lt;pipeline&gt; &lt;sha&gt;</c> — wait for the binding to apply a config commit.</summary>
public sealed class WaitReconcileCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("timeout");
    public string Noun => "wait";
    public string Verb => "reconcile";
    public int RequiredOperands => 2;
    public string HelpLine => "Wait for the binding to apply a config commit; exit 1 if its reconcile fails";
    public string? HelpDetail =>
        """
        pl wait reconcile <pipeline> <sha> [--timeout 20m]

          Blocks until the binding has applied config commit <sha> (a prefix is enough): prints the
          version it switched to. Exits 1 when that commit's reconcile fails. <sha> must touch the
          binding path (or its local dependencies); other commits are never reconciled.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        var sha = cli.Operand(1)!;
        var id = pipeline.Id.ToString();
        return await Waiter.RunAsync(cli, ctx, id, new HashSet<string> { "binding", "version" }, async c =>
        {
            if ((await ctx.Api.GetPipelineBindingStatus(id, c)).TryPickProblems(out var statusProblems, out var b))
                return statusProblems;
            bool Is(string? s) => s?.StartsWith(sha, StringComparison.OrdinalIgnoreCase) == true;
            if (Is(b.LastReconciledSha) && b.Result == ReconcileResult.Success)
                return new WaitCheck.Done(b.Pending is { Held: false } pending && pending.Version == b.ReconciledVersion
                    ? $"{pipeline.Label}: {Short(b.LastReconciledSha)} applied; {pending.Describe()}"
                    : $"{pipeline.Label}: {Short(b.LastReconciledSha)} applied (v{b.ReconciledVersion})", true);
            if (Is(b.ReconcileSha) && b.Result == ReconcileResult.Error && !b.ReconcileInProgress)
                return new WaitCheck.Done($"{pipeline.Label}: reconcile of {Short(b.ReconcileSha)} failed: {string.Join("; ", b.Problems)}", false);
            return new WaitCheck.Pending(b.ReconcileInProgress
                ? $"{pipeline.Label}: reconciling {Short(b.ReconcileSha)}"
                : $"{pipeline.Label}: at {Short(b.LastReconciledSha)}, waiting for {sha}");
        }, ct);
    }
}
