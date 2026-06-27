using Olve.Pipelines.Cli.Api;

namespace Olve.Pipelines.Cli.Commands.Pipelines;

/// <summary><c>pl pipeline get &lt;id&gt;</c> — show a single pipeline.</summary>
public sealed class PipelineGetCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a pipeline by id";
    public string? HelpDetail => "pl pipeline get <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetPipeline(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var pipeline))
            return problems;

        ctx.Output.Emit(pipeline, CliJsonContext.Default.Pipeline, () =>
        {
            ctx.Output.Line($"Id:   {pipeline.Id}");
            ctx.Output.Line($"Name: {pipeline.Name}");
        });
        return Result.Success();
    }
}

/// <summary><c>pl pipeline delete &lt;id&gt;</c> — delete a pipeline and cascade-cancel its jobs.</summary>
public sealed class PipelineDeleteCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "delete";
    public int RequiredOperands => 1;
    public string HelpLine => "Delete a pipeline by id";
    public string? HelpDetail => "pl pipeline delete <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var id = cli.Operand(0)!;
        if ((await ctx.Api.DeletePipeline(id, ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Deleted pipeline {id}.");
        return Result.Success();
    }
}

/// <summary><c>pl pipeline document &lt;id&gt;</c> — export the pipeline's config document as JSON.</summary>
public sealed class PipelineDocumentCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "document";
    public int RequiredOperands => 1;
    public string HelpLine => "Export the pipeline config document (JSON)";
    public string? HelpDetail => "pl pipeline document <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetPipelineDocumentRaw(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var doc))
            return problems;

        ctx.Output.Line(doc);
        return Result.Success();
    }
}
