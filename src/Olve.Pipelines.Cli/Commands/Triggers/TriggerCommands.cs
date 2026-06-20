using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Triggers;

/// <summary><c>pl trigger list &lt;pipelineId&gt;</c> — list a pipeline's triggers.</summary>
public sealed class TriggerListCommand : ICliCommand
{
    public string Noun => "trigger";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List triggers for a pipeline";
    public string? HelpDetail => "pl trigger list <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListTriggers(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var triggers))
            return problems;

        ctx.Output.Emit(triggers, CliJsonContext.Default.TriggerArray, () =>
        {
            var rows = triggers
                .Select(t => (IReadOnlyList<string>)[t.Id.ToString(), t.Name, t.Target.Kind])
                .ToList();
            foreach (var line in Table.Render(["ID", "NAME", "TARGET"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl trigger get &lt;triggerId&gt;</c> — show a single trigger.</summary>
public sealed class TriggerGetCommand : ICliCommand
{
    public string Noun => "trigger";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a trigger by id";
    public string? HelpDetail => "pl trigger get <triggerId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetTrigger(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var trigger))
            return problems;

        ctx.Output.Emit(trigger, CliJsonContext.Default.Trigger, () =>
        {
            ctx.Output.Line($"Id:       {trigger.Id}");
            ctx.Output.Line($"Name:     {trigger.Name}");
            ctx.Output.Line($"Pipeline: {trigger.PipelineId}");
            ctx.Output.Line($"Target:   {trigger.Target.Kind}");
            switch (trigger.Target)
            {
                case ProcessingTriggerTarget p:
                    ctx.Output.Line($"  Step: {p.ProcessingStepId}");
                    break;
                case PollTriggerTarget p:
                    ctx.Output.Line($"  Url:           {p.Url}");
                    ctx.Output.Line($"  ValuePath:     {p.ValuePath}");
                    ctx.Output.Line($"  IntervalSecs:  {p.IntervalSeconds}");
                    break;
            }
        });
        return Result.Success();
    }
}
