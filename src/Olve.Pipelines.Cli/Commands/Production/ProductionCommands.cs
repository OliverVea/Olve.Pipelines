using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Production;

/// <summary><c>pl production list &lt;pipelineId&gt;</c> — list a pipeline's production steps.</summary>
public sealed class ProductionListCommand : ICliCommand
{
    public string Noun => "production";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List production steps for a pipeline";
    public string? HelpDetail => "pl production list <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListProductionSteps(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var steps))
            return problems;

        ctx.Output.Emit(steps, CliJsonContext.Default.ProductionStepArray, () =>
        {
            var rows = steps.Select(s => (IReadOnlyList<string>)[s.Id.ToString(), s.Name]).ToList();
            foreach (var line in Table.Render(["ID", "NAME"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl production get &lt;stepId&gt;</c> — show a single production step.</summary>
public sealed class ProductionGetCommand : ICliCommand
{
    public string Noun => "production";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a production step by id";
    public string? HelpDetail => "pl production get <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetProductionStep(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var step))
            return problems;

        ctx.Output.Emit(step, CliJsonContext.Default.ProductionStep, () =>
        {
            ctx.Output.Line($"Id:       {step.Id}");
            ctx.Output.Line($"Name:     {step.Name}");
            ctx.Output.Line($"Pipeline: {step.PipelineId}");
        });
        return Result.Success();
    }
}

/// <summary><c>pl production config &lt;stepId&gt;</c> — show a production step's image/script/env.</summary>
public sealed class ProductionConfigCommand : ICliCommand
{
    public string Noun => "production";
    public string Verb => "config";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a production step's configuration (image/script/env)";
    public string? HelpDetail => "pl production config <stepId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetProductionStepConfiguration(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var config))
            return problems;

        ctx.Output.Emit(config, CliJsonContext.Default.StepConfiguration,
            () => StepConfigPrinter.Print(ctx.Output, config));
        return Result.Success();
    }
}

/// <summary><c>pl production trigger &lt;pipelineId&gt;</c> — fire all production steps.</summary>
public sealed class ProductionTriggerCommand : ICliCommand
{
    public string Noun => "production";
    public string Verb => "trigger";
    public int RequiredOperands => 1;
    public string HelpLine => "Trigger all production steps for a pipeline";
    public string? HelpDetail => "pl production trigger <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.TriggerProduction(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var group))
            return problems;

        ctx.Output.Emit(group, CliJsonContext.Default.JobGroup,
            () => ctx.Output.Line($"Triggered production. Job group: {group.Id}"));
        return Result.Success();
    }
}
