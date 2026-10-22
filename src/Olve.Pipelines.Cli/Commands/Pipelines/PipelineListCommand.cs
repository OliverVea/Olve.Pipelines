using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands.Runs;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Pipelines;

/// <summary><c>pl pipeline list [--summaries]</c>.</summary>
public sealed class PipelineListCommand : ICliCommand
{
    public string Noun => "pipeline";
    public string Verb => "list";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "summaries" };
    public string HelpLine => "List pipelines with health, last run, active version, running/held runs and blocked steps";
    public string? HelpDetail =>
        """
        pl pipeline list [--summaries]

          --summaries   Also show the bound repo and description

        Status is computed over runs (cancelled/superseded runs ignored; skipped steps are not faults):
          Healthy              nothing running; the latest settled run succeeded
          Running (Healthy)    a run is running; the latest settled run did not fail
          Running (Unhealthy)  a run is running; the latest settled run failed
          Unhealthy            nothing running; the latest settled run failed
          Idle                 no settled run and nothing running
        Details: `pl pipeline health <pipeline>`.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListPipelineSummaries(ct)).TryPickProblems(out var problems, out var summaries))
            return problems;

        var detailed = cli.HasFlag("summaries");

        ctx.Output.Emit(summaries, CliJsonContext.Default.PipelineSummaryArray, () =>
        {
            var rows = summaries.Select(s =>
            {
                var h = s.Health;
                IReadOnlyList<string> common =
                [
                    s.Id.ToString(), s.Name, h.Status, LastRun(h), h.Activation?.ToString() ?? "-",
                    $"{h.RunningRuns}/{h.HeldRuns.Length}",
                    h.BlockedSteps.Length == 0 ? "-" : string.Join(",", h.BlockedSteps),
                ];
                return detailed ? (IReadOnlyList<string>)[.. common, s.Repo ?? "-", s.Description ?? ""] : common;
            }).ToList();

            IReadOnlyList<string> headers = ["ID", "NAME", "STATUS", "LAST RUN", "VERSION", "RUNNING/HELD", "BLOCKED"];
            foreach (var line in Table.Render(detailed ? [.. headers, "REPO", "DESCRIPTION"] : headers, rows))
                ctx.Output.Line(line);
        });

        return Result.Success();
    }

    private static string LastRun(PipelineHealth h)
        => h.LastRun is { } r ? $"#{r.Number} {r.Status} {RunCommandSupport.Time(r.CreatedAt)}" : "-";
}
