using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Processing;

/// <summary><c>pl processing list &lt;pipelineId&gt;</c> — list a pipeline's processing steps (in order).</summary>
public sealed class ProcessingListCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List processing steps for a pipeline";
    public string? HelpDetail => "pl processing list <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListProcessingSteps(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var steps))
            return problems;

        ctx.Output.Emit(steps, CliJsonContext.Default.ProcessingStepArray, () =>
        {
            var rows = steps.OrderBy(s => s.Order)
                .Select(s => (IReadOnlyList<string>)[s.Order.ToString(), s.Id.ToString(), s.Name]).ToList();
            foreach (var line in Table.Render(["ORDER", "ID", "NAME"], rows))
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
    public string HelpLine => "List promotion-gate state for all processing steps";
    public string? HelpDetail => "pl processing promotions <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListProcessingStepPromotions(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var promotions))
            return problems;

        ctx.Output.Emit(promotions, CliJsonContext.Default.StepPromotionStateArray, () =>
        {
            var rows = promotions
                .Select(p => (IReadOnlyList<string>)[p.ProcessingStepId.ToString(), p.Blocked ? "blocked" : "enabled"])
                .ToList();
            foreach (var line in Table.Render(["STEP", "GATE"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl processing promotion &lt;stepId&gt;</c> — promotion-gate state for one step.</summary>
public sealed class ProcessingPromotionCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "promotion";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a processing step's promotion-gate state";
    public string? HelpDetail => "pl processing promotion <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetProcessingStepPromotion(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var state))
            return problems;

        ctx.Output.Emit(state, CliJsonContext.Default.PromotionState,
            () => ctx.Output.Line(state.Blocked ? "blocked" : "enabled"));
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
        if ((await ctx.Api.SetProcessingStepPromotion(cli.Operand(0)!, blocked: true, ct)).TryPickProblems(out var problems, out var state))
            return problems;

        ctx.Output.Emit(state, CliJsonContext.Default.PromotionState,
            () => ctx.Output.Line(state.Blocked ? "blocked" : "enabled"));
        return Result.Success();
    }
}

/// <summary><c>pl processing unblock &lt;stepId&gt;</c> — release the promotion gate.</summary>
public sealed class ProcessingUnblockCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "unblock";
    public int RequiredOperands => 1;
    public string HelpLine => "Unblock promotion for a processing step (release the gate)";
    public string? HelpDetail => "pl processing unblock <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.SetProcessingStepPromotion(cli.Operand(0)!, blocked: false, ct)).TryPickProblems(out var problems, out var state))
            return problems;

        ctx.Output.Emit(state, CliJsonContext.Default.PromotionState,
            () => ctx.Output.Line(state.Blocked ? "blocked" : "enabled"));
        return Result.Success();
    }
}

/// <summary><c>pl processing re-promote &lt;stepId&gt;</c> — redrive the step's last bundle.</summary>
public sealed class ProcessingRePromoteCommand : ICliCommand
{
    public string Noun => "processing";
    public string Verb => "re-promote";
    public int RequiredOperands => 1;
    public string HelpLine => "Re-run a processing step with its last artifact bundle";
    public string? HelpDetail => "pl processing re-promote <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.RePromoteProcessingStep(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var group))
            return problems;

        ctx.Output.Emit(group, CliJsonContext.Default.JobGroup,
            () => ctx.Output.Line($"Re-promoted. Job group: {group.Id}"));
        return Result.Success();
    }
}
