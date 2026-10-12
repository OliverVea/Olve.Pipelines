using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Processing;

/// <summary>
/// <c>pl processing list &lt;pipelineId&gt;</c> — list a pipeline's processing steps (in order), with each
/// step's gate and the build Promote would use right now.
/// </summary>
public sealed class ProcessingListCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List processing steps for a pipeline (with gate and what Promote would use)";
    public string? HelpDetail => """
        pl processing list <pipelineId>

        PROMOTE is the build `pl processing promote <stepId>` would run now: the bundle that last
        succeeded at the step before (production, for the first step), or why there is none.
        --json prints the steps only; `pl processing promotions <pipelineId> --json` has the rest.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListProcessingSteps(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var steps))
            return problems;

        // Table only, best-effort: if the promotion read model can't be fetched the steps still list.
        var promotions = new Dictionary<Guid, StepPromotionState>();
        if (!ctx.Json && !(await ctx.Api.ListProcessingStepPromotions(cli.Operand(0)!, ct)).TryPickProblems(out _, out var states))
            promotions = states.ToDictionary(p => p.ProcessingStepId);

        ctx.Output.Emit(steps, CliJsonContext.Default.ProcessingStepArray, () =>
        {
            var rows = steps.OrderBy(s => s.Order)
                .Select(s => (IReadOnlyList<string>)[
                    s.Order.ToString(), s.Id.ToString(), s.Name,
                    promotions.TryGetValue(s.Id, out var p) ? (p.Blocked ? "blocked" : "enabled") : "?",
                    promotions.TryGetValue(s.Id, out p) ? PromotionText.Cell(p.Promote, p.Source) : "?"])
                .ToList();
            foreach (var line in Table.Render(["ORDER", "ID", "NAME", "GATE", "PROMOTE"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl processing get &lt;stepId&gt;</c> — show a single processing step.</summary>
public sealed class ProcessingGetCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a processing step by id";
    public string? HelpDetail => "pl processing get <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetProcessingStep(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var step))
            return problems;

        ctx.Output.Emit(step, CliJsonContext.Default.ProcessingStep, () =>
        {
            ctx.Output.Line($"Id:       {step.Id}");
            ctx.Output.Line($"Name:     {step.Name}");
            ctx.Output.Line($"Order:    {step.Order}");
            ctx.Output.Line($"Pipeline: {step.PipelineId}");
        });
        return Result.Success();
    }
}

/// <summary><c>pl processing config &lt;stepId&gt;</c> — show a processing step's image/script/env.</summary>
public sealed class ProcessingConfigCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "config";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a processing step's configuration (image/script/env)";
    public string? HelpDetail => "pl processing config <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetProcessingStepConfiguration(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var config))
            return problems;

        ctx.Output.Emit(config, CliJsonContext.Default.StepConfiguration,
            () => StepConfigPrinter.Print(ctx.Output, config));
        return Result.Success();
    }
}

/// <summary><c>pl processing promotions &lt;pipelineId&gt;</c> — promotion-gate state for all steps.</summary>
public sealed class ProcessingPromotionsCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "promotions";
    public int RequiredOperands => 1;
    public string HelpLine => "List promotion-gate state and what Promote would use, for all processing steps";
    public string? HelpDetail => """
        pl processing promotions <pipelineId>

        PROMOTE is what `pl processing promote` would run now; ON UNBLOCK (blocked steps only) is
        the promotion `pl processing unblock` would start.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListProcessingStepPromotions(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var promotions))
            return problems;

        ctx.Output.Emit(promotions, CliJsonContext.Default.StepPromotionStateArray, () =>
        {
            var rows = promotions
                .Select(p => (IReadOnlyList<string>)[
                    p.ProcessingStepId.ToString(), p.StepName, p.Blocked ? "blocked" : "enabled",
                    PromotionText.Cell(p.Promote, p.Source),
                    p.OnUnblock is { } u ? PromotionText.Cell(u, p.Source) : ""])
                .ToList();
            foreach (var line in Table.Render(["STEP", "NAME", "GATE", "PROMOTE", "ON UNBLOCK"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl processing promotion &lt;stepId&gt;</c> — promotion-gate state for one step, and what Promote would use.</summary>
public sealed class ProcessingPromotionCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "promotion";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a processing step's promotion gate and the build Promote would use";
    public string? HelpDetail => "pl processing promotion <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetProcessingStepPromotion(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var state))
            return problems;

        ctx.Output.Emit(state, CliJsonContext.Default.StepPromotionState, () =>
        {
            ctx.Output.Line($"Step:         {state.StepName} ({state.ProcessingStepId})");
            ctx.Output.Line($"Gate:         {(state.Blocked ? "blocked" : "enabled")}");
            ctx.Output.Line($"Source:       {PromotionText.SourceLabel(state.Source)}");
            ctx.Output.Line($"Source last:  {(state.SourceLastSuccess is { } s ? PromotionText.Describe(s) : "-")}");
            ctx.Output.Line($"Step last:    {(state.LastSuccess is { } l ? PromotionText.Describe(l) : "-")}");
            ctx.Output.Line($"Promote:      {state.Promote.Message}");
            if (state.OnUnblock is { } u)
                ctx.Output.Line($"On unblock:   {u.Message}");
        });
        return Result.Success();
    }
}

/// <summary><c>pl processing block &lt;stepId&gt;</c> — engage the promotion gate (the brake).</summary>
public sealed class ProcessingBlockCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "block";
    public int RequiredOperands => 1;
    public string HelpLine => "Block promotion for a processing step (engage the gate)";
    public string? HelpDetail => "pl processing block <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.SetProcessingStepPromotion(cli.Operand(0)!, blocked: true, expectedPromotion: null, ct)).TryPickProblems(out var problems, out var state))
            return problems;

        ctx.Output.Emit(state, CliJsonContext.Default.PromotionState,
            () => ctx.Output.Line(state.Blocked ? "blocked" : "enabled"));
        return Result.Success();
    }
}

/// <summary>
/// <c>pl processing unblock &lt;stepId&gt;</c> — release the promotion gate. If the step was blocked, the
/// server also starts the promotion that is due (if any); this prints which build, or why none.
/// </summary>
public sealed class ProcessingUnblockCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "unblock";
    public int RequiredOperands => 1;
    public string HelpLine => "Unblock promotion for a processing step, and start the promotion that is due";
    public string? HelpDetail => """
        pl processing unblock <stepId> [--expect <bundleId>|none]

        Releases the gate. If the step was blocked and the bundle that last succeeded at the step
        before it is newer than what this step last ran successfully, that promotion starts now.
        Prints the build it started, or why it started none. Never goes backwards.
        Check first with `pl processing promotion <stepId>` (the "On unblock" line).

          --expect <bundleId>|none  Refuse (and stay blocked) unless unblocking would start that
                                    bundle (full id), or start nothing ("none").
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.SetProcessingStepPromotion(cli.Operand(0)!, blocked: false, cli.Option("expect"), ct)).TryPickProblems(out var problems, out var state))
            return problems;

        ctx.Output.Emit(state, CliJsonContext.Default.PromotionState, () =>
        {
            ctx.Output.Line(state.Blocked ? "blocked" : "enabled");
            if (state.Promotion is { } p)
            {
                ctx.Output.Line(p.Message);
                if (p.Started && p.JobGroupId is { } groupId)
                    ctx.Output.Line($"Job group: {groupId}");
            }
        });
        return Result.Success();
    }
}

/// <summary>
/// <c>pl processing promote &lt;stepId&gt; [--expect &lt;bundleId&gt;]</c> — run the step with the bundle that
/// last succeeded at the step before it.
/// </summary>
public sealed class ProcessingPromoteCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "promote";
    public int RequiredOperands => 1;
    public string HelpLine => "Run a processing step with the bundle that last succeeded at the step before it";
    public string? HelpDetail => """
        pl processing promote <stepId> [--expect <bundleId>]

        Runs the step with the bundle that last succeeded at the previous processing step (for the
        first step: the latest completed production run). With nothing newer through the previous
        step, that is the same bundle again — the way to retry a failed deploy. Refused while the
        step is blocked, while a job for it is scheduled or running, and when the bundle would be
        older than what the step last ran. See which build it would use with
        `pl processing promotion <stepId>`.

          --expect <bundleId>  Refuse unless that is the bundle it would use (full id).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.PromoteProcessingStep(cli.Operand(0)!, cli.Option("expect"), ct)).TryPickProblems(out var problems, out var result))
            return problems;

        ctx.Output.Emit(result, CliJsonContext.Default.PromoteResult, () =>
        {
            ctx.Output.Line(result.Message);
            ctx.Output.Line($"Job group: {result.JobGroupId}");
        });
        return Result.Success();
    }
}

/// <summary>
/// <c>pl processing re-promote</c> — removed. Kept for one release so muscle memory gets an explanation
/// instead of "unknown command"; it calls nothing.
/// </summary>
public sealed class ProcessingRePromoteCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "re-promote";
    public int RequiredOperands => 0;
    public string HelpLine => "Removed — use `pl processing promote <stepId>`";
    public string? HelpDetail => """
        pl processing re-promote (removed)

        re-promote re-ran a step with its own last bundle, which after a staged release redeployed
        the previous version. Use `pl processing promote <stepId>`: it runs the bundle that last
        succeeded at the step before. Retrying a failed deploy is the same command.
        """;

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
        => Task.FromResult(Result.Failure(new ResultProblem(
            "`pl processing re-promote` has been removed. Use `pl processing promote <stepId>`, which runs the bundle " +
            "that last succeeded at the previous step (see `pl processing promotion <stepId>` for which build).")));
}

/// <summary>Shared formatting for promotion bundles in tables and messages.</summary>
internal static class PromotionText
{
    public static string Short(Guid id) => id.ToString("N")[..8];

    public static string Describe(PromotionBundle b)
        => $"build of {b.BuiltAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC (bundle {Short(b.ArtifactBundleId)}, {b.ArtifactBundleId})";

    public static string SourceLabel(PromotionSource source) => source.IsProduction ? "production" : source.Name;

    /// <summary>Compact table cell: "2026-10-12 05:49 3f2a9c1e from test-after-beta", or "- (reason)".</summary>
    public static string Cell(PromotionOption option, PromotionSource source)
        => option.Allowed && option.Bundle is { } b
            ? $"{b.BuiltAt.UtcDateTime:yyyy-MM-dd HH:mm} {Short(b.ArtifactBundleId)} from {SourceLabel(source)}"
            : $"- ({option.Reason})";
}
