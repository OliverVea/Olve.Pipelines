using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Bundles;

/// <summary><c>pl bundle list &lt;pipelineId&gt;</c> — list a pipeline's artifact bundles.</summary>
public sealed class BundleListCommand : ICliCommand
{
    public string Noun => "bundle";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List artifact bundles for a pipeline";
    public string? HelpDetail => "pl bundle list <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListArtifactBundles(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var bundles))
            return problems;

        ctx.Output.Emit(bundles, CliJsonContext.Default.ArtifactBundleArray, () =>
        {
            var rows = bundles
                .Select(b => (IReadOnlyList<string>)[b.Id.ToString(), b.Status.ToString(), b.CreatedAt.ToString("u")])
                .ToList();
            foreach (var line in Table.Render(["ID", "STATUS", "CREATED"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl bundle get &lt;bundleId&gt;</c> — show a single artifact bundle.</summary>
public sealed class BundleGetCommand : ICliCommand
{
    public string Noun => "bundle";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get an artifact bundle by id";
    public string? HelpDetail => "pl bundle get <bundleId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetArtifactBundle(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var bundle))
            return problems;

        ctx.Output.Emit(bundle, CliJsonContext.Default.ArtifactBundle, () =>
        {
            ctx.Output.Line($"Id:       {bundle.Id}");
            ctx.Output.Line($"Pipeline: {bundle.PipelineId}");
            ctx.Output.Line($"Status:   {bundle.Status}");
            ctx.Output.Line($"Created:  {bundle.CreatedAt:u}");
        });
        return Result.Success();
    }
}
