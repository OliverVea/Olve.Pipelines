using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Pipelines;

/// <summary><c>pl pipeline list [--summaries]</c> — list all pipelines (or richer summaries).</summary>
public sealed class PipelineListCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "list";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "summaries" };
    public string HelpLine => "List pipelines with aggregate status (--summaries for repo + step health)";
    public string? HelpDetail =>
        """
        pl pipeline list [--summaries]   List pipelines with their aggregate status

          --summaries   Also include repo binding + per-step health and last-changed time

        Status is derived from each pipeline's step strip:
          Healthy              every step is green
          Running (Healthy)    a step is running, none have failed
          Running (Unhealthy)  a step is running and at least one has failed
          Unhealthy            nothing running but at least one step has failed
          Idle                 no steps, or not yet fully run
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        // The summary endpoint carries the per-step strip the aggregate status is derived from,
        // so both the default and --summaries views are backed by it (one call, no N+1 fan-out).
        if ((await ctx.Api.ListPipelineSummaries(ct)).TryPickProblems(out var problems, out var summaries))
            return problems;

        var detailed = cli.HasFlag("summaries");

        ctx.Output.Emit(summaries, CliJsonContext.Default.PipelineSummaryArray, () =>
        {
            if (detailed)
            {
                var rows = summaries
                    .Select(s => (IReadOnlyList<string>)
                        [s.Id.ToString(), s.Name, s.Repo ?? "-", s.Status, $"{s.Steps.Length} step(s)"])
                    .ToList();
                foreach (var line in Table.Render(["ID", "NAME", "REPO", "STATUS", "STEPS"], rows))
                    ctx.Output.Line(line);
            }
            else
            {
                var rows = summaries
                    .Select(s => (IReadOnlyList<string>)[s.Id.ToString(), s.Name, s.Status])
                    .ToList();
                foreach (var line in Table.Render(["ID", "NAME", "STATUS"], rows))
                    ctx.Output.Line(line);
            }
        });

        return Result.Success();
    }
}
