using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands.Runs;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Inputs;

/// <summary><c>pl input list</c> — every step waiting for external input, across pipelines.</summary>
public sealed class InputListCommand : ICliCommand
{
    public string Noun => "input";
    public string Verb => "list";
    public string HelpLine => "Steps waiting for external input, across all pipelines";
    public string? HelpDetail =>
        """
        pl input list

          Every step that waits for an answer, oldest first, by address. Answer one with
          `pl input answer <address>`, or decline it with `pl input decline <address>`.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListWaitingAttempts(ct)).TryPickProblems(out var problems, out var waiting))
            return problems;

        ctx.Output.Emit(waiting, CliJsonContext.Default.ActiveAttemptArray, () =>
        {
            if (waiting.Length == 0)
            {
                ctx.Output.Line("Nothing waits for input.");
                return;
            }

            var rows = waiting.Select(a => (IReadOnlyList<string>)[RunAddress.Format(a.PipelineName, a.Run, a.Step), Time(a.CreatedAt)]).ToList();
            foreach (var line in Table.Render(["STEP", "WAITING SINCE"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl input answer &lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;</c> — give an external input's value.</summary>
public sealed class InputAnswerCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("name", "field", "value");
    public string Noun => "input";
    public string Verb => "answer";
    public int RequiredOperands => 1;
    public string HelpLine => "Answer a step's external input";
    public string? HelpDetail =>
        """
        pl input answer <pipeline>#<run>/<step> [--name <input>] [--value <json|@file|@->] [--field k=v ...]

          Gives the value of an external input of a step that waits for it. --value is the whole JSON
          object (inline, @file, or @- for stdin) and each --field sets one field, typed by the input's
          model. --name picks the input when the step has several.
          The step runs once every external input is answered.

            pl input answer myapp#12/approve --field approved=true --field comment="looks good"
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if (addr.Attempt is not null)
            return new ResultProblem("Answer targets the step's waiting attempt; drop the @{0} from the address.", addr.Attempt);

        var name = cli.Option("name");
        var fields = cli.Options("field");
        var schema = fields.Count > 0 ? await ModelSchemaAsync(ctx, addr, name, ct) : null;
        if (BuildInput(cli.Option("value"), fields, schema).TryPickProblems(out var valueProblems, out var value))
            return valueProblems;

        if ((await ctx.Api.AnswerStepInput(addr.Id, addr.Run, addr.Step, name, value, ct)).TryPickProblems(out var answerProblems, out var attempt))
            return answerProblems;

        ctx.Output.Emit(attempt, CliJsonContext.Default.Attempt, () =>
            ctx.Output.Line($"{RunAddress.Format(addr.Label, attempt.RunNumber, attempt.StepName, attempt.Number)}  {attempt.Status.Kind}"));
        return Result.Success();
    }

    /// <summary>The external input's model schema in the run's pinned manifest, to type --field values. Best-effort: null when unknown.</summary>
    private static async Task<JsonObject?> ModelSchemaAsync(CommandContext ctx, ResolvedAddress addr, string? name, CancellationToken ct)
    {
        if ((await ctx.Api.GetRun(addr.Id, addr.Run, ct)).TryPickProblems(out _, out var run)
            || (await ctx.Api.GetPipelineVersion(addr.Id, run.Run.VersionNumber, ct)).TryPickProblems(out _, out var version))
            return null;
        try
        {
            var manifest = JsonNode.Parse(version.Manifest);
            var inputs = FindStep(manifest?["stages"], addr.Step)?["externalInputs"] as JsonObject;
            var input = name is not null ? inputs?[name] : inputs?.Count == 1 ? inputs.First().Value : null;
            var model = input?["model"]?.GetValue<string>();
            var schemas = model is null ? null : manifest?["models"]?[model];
            return (schemas?["input"] ?? schemas?["output"]) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonNode? FindStep(JsonNode? stages, string step)
    {
        foreach (var stage in stages?.AsArray() ?? [])
        {
            foreach (var s in stage?["steps"]?.AsArray() ?? [])
                if (s?["name"]?.GetValue<string>() == step) return s;
            if (FindStep(stage?["stages"], step) is { } nested) return nested;
            foreach (var (_, branch) in stage?["cases"]?.AsObject() ?? [])
                if (FindStep(branch, step) is { } inCase) return inCase;
        }
        return null;
    }
}

/// <summary><c>pl input decline &lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;</c> — fail a waiting step without running it.</summary>
public sealed class InputDeclineCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("comment");
    public string Noun => "input";
    public string Verb => "decline";
    public int RequiredOperands => 1;
    public string HelpLine => "Decline a step that waits for input (it fails without running)";
    public string? HelpDetail =>
        """
        pl input decline <pipeline>#<run>/<step> [--comment <text>]

          Fails the waiting step without running it; the reason names you and the comment. The run
          fails at the stage barrier, like after any failed step.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolveStepAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var addr))
            return problems;
        if (addr.Attempt is not null)
            return new ResultProblem("Decline targets the step's waiting attempt; drop the @{0} from the address.", addr.Attempt);

        if ((await ctx.Api.DeclineStepInput(addr.Id, addr.Run, addr.Step, cli.Option("comment"), ct)).TryPickProblems(out var declineProblems, out var attempt))
            return declineProblems;

        ctx.Output.Emit(attempt, CliJsonContext.Default.Attempt, () =>
            ctx.Output.Line($"{RunAddress.Format(addr.Label, attempt.RunNumber, attempt.StepName, attempt.Number)}  {attempt.Status.Kind}"));
        return Result.Success();
    }
}
