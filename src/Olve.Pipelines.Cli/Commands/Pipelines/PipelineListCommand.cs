using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Pipelines;

/// <summary><c>pl pipeline list [--summaries]</c> — list all pipelines (or richer summaries).</summary>
public sealed class PipelineListCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "list";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "summaries" };
    public string HelpLine => "List pipelines (--summaries for repo + step health)";
    public string? HelpDetail =>
        """
        pl pipeline list [--summaries]   List pipelines

          --summaries   Include repo binding + per-step health and last-changed time
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (cli.HasFlag("summaries"))
        {
            if ((await ctx.Api.ListPipelineSummaries(ct)).TryPickProblems(out var problems, out var summaries))
                return problems;

            ctx.Output.Emit(summaries, CliJsonContext.Default.PipelineSummaryArray, () =>
            {
                var rows = summaries
                    .Select(s => (IReadOnlyList<string>)
                        [s.Id.ToString(), s.Name, s.Repo ?? "-", $"{s.Steps.Length} step(s)"])
                    .ToList();
                foreach (var line in Table.Render(["ID", "NAME", "REPO", "STEPS"], rows))
                    ctx.Output.Line(line);
            });

            return Result.Success();
        }

        if ((await ctx.Api.ListPipelines(ct)).TryPickProblems(out var listProblems, out var pipelines))
            return listProblems;

        ctx.Output.Emit(pipelines, CliJsonContext.Default.PipelineArray, () =>
        {
            var rows = pipelines
                .Select(p => (IReadOnlyList<string>)[p.Id.ToString(), p.Name])
                .ToList();
            foreach (var line in Table.Render(["ID", "NAME"], rows))
                ctx.Output.Line(line);
        });

        return Result.Success();
    }
}
