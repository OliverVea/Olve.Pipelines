using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Pipelines;

/// <summary><c>pl pipeline get &lt;id&gt;</c> — show a single pipeline.</summary>
public sealed class PipelineGetCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a pipeline by id or name";
    public string? HelpDetail => "pl pipeline get <pipeline>   (id or name)";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await PipelineResolver.ResolveAsync(ctx.Api, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var resolved))
            return resolveProblems;
        if ((await ctx.Api.GetPipeline(resolved.Id.ToString(), ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;

        ctx.Output.Emit(pipeline, CliJsonContext.Default.Pipeline, () =>
        {
            ctx.Output.Line($"Id:   {pipeline.Id}");
            ctx.Output.Line($"Name: {pipeline.Name}");
        });
        return Result.Success();
    }
}

/// <summary><c>pl pipeline delete &lt;pipeline&gt;</c>.</summary>
public sealed class PipelineDeleteCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "delete";
    public int RequiredOperands => 1;
    public string HelpLine => "Delete a pipeline (with its binding, runs and secrets)";
    public string? HelpDetail => "pl pipeline delete <pipeline>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        if ((await ctx.Api.DeletePipeline(pipeline.Id.ToString(), ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Deleted pipeline {pipeline.Label} ({pipeline.Id}).");
        return Result.Success();
    }
}
