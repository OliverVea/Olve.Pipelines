using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Pipelines.Cli.Commands.Runs;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Pipelines;

/// <summary><c>pl pipeline versions &lt;pipeline&gt;</c> — versions and the activation state.</summary>
public sealed class PipelineVersionsCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "versions";
    public int RequiredOperands => 1;
    public string HelpLine => "List a pipeline's versions and which one is active";
    public string? HelpDetail =>
        """
        pl pipeline versions <pipeline>

          Each version is a distinct manifest added by reconcile (linked to its commit). `>` marks
          the version runs start on. Steps/models list their entity versions (<id>.<version>).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.ListPipelineVersions(pipeline.Id.ToString(), ct)).TryPickProblems(out var listProblems, out var versions))
            return listProblems;

        ctx.Output.Emit(versions, CliJsonContext.Default.PipelineVersionsResponse, () =>
        {
            ctx.Output.Line($"activation: {versions.Activation?.ToString() ?? "none (no version yet)"}");
            ctx.Output.Line();
            var current = versions.Activation?.CurrentVersion;
            var rows = versions.Versions.OrderByDescending(v => v.Number).Select(v => (IReadOnlyList<string>)
            [
                $"{(v.Number == current ? ">" : " ")} v{v.Number}", Short(v.CommitSha), Time(v.CreatedAt),
                string.Join(" ", v.StepVersions.Select(kv => $"{kv.Key}.{kv.Value}")),
            ]).ToList();
            foreach (var line in Table.Render(["  VERSION", "COMMIT", "CREATED", "STEPS"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl pipeline manifest &lt;pipeline&gt; [--version n]</c> — a version's manifest.</summary>
public sealed class PipelineManifestCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "manifest";
    public int RequiredOperands => 1;
    public string HelpLine => "Show a version's manifest (default: the version runs start on)";
    public string? HelpDetail =>
        """
        pl pipeline manifest <pipeline> [--version <n>] [--json]

          Summarizes the manifest like `pl dev describe` (input model, secrets, triggers, stages,
          `when` blocks). --json prints the canonical manifest JSON itself.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        var id = pipeline.Id.ToString();

        int version;
        if (cli.Option("version") is { } text)
        {
            if (!int.TryParse(text.TrimStart('v'), out version) || version < 1)
                return new ResultProblem("--version must be a positive integer.");
        }
        else
        {
            if ((await ctx.Api.ListPipelineVersions(id, ct)).TryPickProblems(out var listProblems, out var versions))
                return listProblems;
            if (versions.Activation?.CurrentVersion is not { } current)
                return new ResultProblem("Pipeline '{0}' has no active version yet.", pipeline.Label);
            version = current;
        }

        if ((await ctx.Api.GetPipelineVersion(id, version, ct)).TryPickProblems(out var getProblems, out var pv))
            return getProblems;

        if (ctx.Json)
        {
            ctx.Output.Line(pv.Manifest);
            return Result.Success();
        }

        ctx.Output.Line($"v{pv.Number} · {pv.ManifestHash} · commit {Short(pv.CommitSha)} · {Time(pv.CreatedAt)}");
        ctx.Output.Line();
        if (DevManifest.Parse(pv.Manifest).TryPickProblems(out var parseProblems, out var manifest))
            return parseProblems;
        var writer = new StringWriter();
        DevDescribeCommand.Render(writer, manifest);
        ctx.Output.Write(writer.ToString());
        return Result.Success();
    }
}

/// <summary><c>pl pipeline switch &lt;pipeline&gt; &lt;n&gt; [--cancel-running]</c> — make version n the active one.</summary>
public sealed class PipelineSwitchCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "switch";
    public int RequiredOperands => 2;
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "cancel-running" };
    public string HelpLine => "Switch the active version (rollback = switch to an older one)";
    public string? HelpDetail =>
        """
        pl pipeline switch <pipeline> <version> [--cancel-running]

          The current version drains (starts no new runs; in-flight jobs finish), then <version>
          becomes active. Triggers during draining are queued and start on <version>.
          --cancel-running cancels the old version's in-flight jobs instead of waiting.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (!int.TryParse(cli.Operand(1)!.TrimStart('v'), out var version) || version < 1)
            return new ResultProblem("Version must be a positive integer (e.g. 7), got '{0}'.", cli.Operand(1)!);
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.SwitchPipelineVersion(pipeline.Id.ToString(), version, cli.HasFlag("cancel-running"), ct))
            .TryPickProblems(out var switchProblems, out var activation))
            return switchProblems;

        ctx.Output.Emit(activation, CliJsonContext.Default.PipelineActivation, () => ctx.Output.Line(activation.ToString()));
        return Result.Success();
    }
}

/// <summary><c>pl pipeline health &lt;pipeline&gt;</c> — run-based health.</summary>
public sealed class PipelineHealthCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "health";
    public int RequiredOperands => 1;
    public string HelpLine => "Run-based health: last run, running/held runs, gates, activation";
    public string? HelpDetail =>
        """
        pl pipeline health <pipeline>

          Running if any run is running, else the latest succeeded/failed run's outcome
          (cancelled and superseded runs are ignored; skipped steps never count as unhealthy).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.GetPipelineHealth(pipeline.Id.ToString(), ct)).TryPickProblems(out var healthProblems, out var h))
            return healthProblems;

        ctx.Output.Emit(h, CliJsonContext.Default.PipelineHealth, () =>
        {
            ctx.Output.Line($"status:      {h.Status}");
            ctx.Output.Line($"activation:  {h.Activation?.ToString() ?? "none"}");
            ctx.Output.Line($"last run:    {(h.LastRun is { } l ? $"#{l.Number} {l.Status} ({Time(l.CreatedAt)})" : "-")}");
            ctx.Output.Line($"settled:     {(h.LastSettledRun is { } s ? $"#{s.Number} {s.Status} ({Time(s.FinishedAt)})" : "-")}");
            ctx.Output.Line($"running:     {h.RunningRuns}");
            ctx.Output.Line($"blocked:     {(h.BlockedSteps.Length == 0 ? "-" : string.Join(", ", h.BlockedSteps))}");
            ctx.Output.Line($"held runs:   {(h.HeldRuns.Length == 0 ? "-" : string.Join(", ", h.HeldRuns.Select(r => $"#{r.Number} at {r.HeldAt}")))}");
        });
        return Result.Success();
    }
}
