using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Steps;

/// <summary><c>pl step blocked &lt;pipeline&gt;</c> — the blocked steps.</summary>
public sealed class StepBlockedCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "blocked";
    public int RequiredOperands => 1;
    public string HelpLine => "List a pipeline's blocked steps";
    public string? HelpDetail =>
        """
        pl step blocked <pipeline>

          Blocks are keyed on step name and survive version switches. A blocked step gets no
          attempt: a run reaching it is held there until the step is unblocked.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;
        if ((await ctx.Api.ListBlockedSteps(pipeline.Id.ToString(), ct)).TryPickProblems(out var listProblems, out var blocked))
            return listProblems;

        ctx.Output.Emit(blocked, CliJsonContext.Default.StringArray, () => StepBlockOutput.PrintBlocked(ctx, blocked));
        return Result.Success();
    }
}

/// <summary><c>pl step block &lt;pipeline&gt;/&lt;step&gt;</c>.</summary>
public sealed class StepBlockCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "block";
    public int RequiredOperands => 1;
    public string HelpLine => "Block a step: runs reaching it are held there (while working on the pipeline)";
    public string? HelpDetail =>
        """
        pl step block <pipeline>/<step>

          Holds every run that reaches the step, however it started, until unblocked. Use it
          sparingly (while working on the pipeline or its target), not as an approval step.
          Prints the pipeline's blocked steps afterwards.
        """;

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct) =>
        StepBlockOutput.ToggleAsync(cli, ctx, block: true, ct);
}

/// <summary><c>pl step unblock &lt;pipeline&gt;/&lt;step&gt;</c>.</summary>
public sealed class StepUnblockCommand : ICliCommand
{
    public string Noun => "step";
    public string Verb => "unblock";
    public int RequiredOperands => 1;
    public string HelpLine => "Unblock a step: held runs resume";
    public string? HelpDetail => "pl step unblock <pipeline>/<step>   Prints the pipeline's blocked steps afterwards.";

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct) =>
        StepBlockOutput.ToggleAsync(cli, ctx, block: false, ct);
}

internal static class StepBlockOutput
{
    public static async Task<Result> ToggleAsync(CliArgs cli, CommandContext ctx, bool block, CancellationToken ct)
    {
        if (StepRef.Parse(cli.Operand(0)!).TryPickProblems(out var refProblems, out var stepRef))
            return refProblems;
        if ((await ResolvePipelineAsync(ctx, stepRef.Pipeline, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;

        var id = pipeline.Id.ToString();
        var result = block
            ? await ctx.Api.BlockStep(id, stepRef.Step, ct)
            : await ctx.Api.UnblockStep(id, stepRef.Step, ct);
        if (result.TryPickProblems(out var toggleProblems, out var blocked))
            return toggleProblems;

        ctx.Output.Emit(blocked, CliJsonContext.Default.StringArray, () =>
        {
            ctx.Output.Line($"{(block ? "blocked" : "unblocked")} {stepRef}");
            PrintBlocked(ctx, blocked);
        });
        return Result.Success();
    }

    public static void PrintBlocked(CommandContext ctx, string[] blocked) =>
        ctx.Output.Line(blocked.Length == 0 ? "no blocked steps" : $"blocked: {string.Join(", ", blocked)}");
}
