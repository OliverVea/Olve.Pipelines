using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Attempts;

/// <summary><c>pl attempts</c> — what's running: every scheduled/in-progress attempt across pipelines.</summary>
public sealed class AttemptListCommand : ICliCommand
{
    public string Noun => "attempts";
    public string Verb => "";
    public string HelpLine => "What's running: scheduled/in-progress attempts across all pipelines";
    public string? HelpDetail =>
        """
        pl attempts

          Lists every scheduled or in-progress step attempt on the instance, oldest first, by address
          (<pipeline>#<run>/<step>@<attempt>). Cancel one with `pl step cancel <pipeline>#<run>/<step>`.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListActiveAttempts(ct)).TryPickProblems(out var problems, out var attempts))
            return problems;

        ctx.Output.Emit(attempts, CliJsonContext.Default.ActiveAttemptArray, () =>
        {
            if (attempts.Length == 0)
            {
                ctx.Output.Line("Nothing is running.");
                return;
            }

            var rows = attempts.Select(a => (IReadOnlyList<string>)
                [RunAddress.Format(a.PipelineName, a.Run, a.Step, a.Attempt), a.Status, Time(a.CreatedAt), Time(a.StartedAt)]).ToList();
            foreach (var line in Table.Render(["ATTEMPT", "STATUS", "CREATED", "STARTED"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}
