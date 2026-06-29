using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Bindings;

/// <summary>
/// <c>pl binding create &lt;repo&gt;</c> — create a pipeline already bound to a git repo (GitOps).
/// One call composes pipeline + binding; the reconcile loop then materializes the steps from the
/// repo's config directory. Mirrors the <c>setup-pipeline</c> bootstrap's bind step.
/// </summary>
public sealed class BindingCreateCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "create";
    public int RequiredOperands => 1;
    public string HelpLine => "Create a pipeline bound to a repo (GitOps)";
    public string? HelpDetail =>
        """
        pl binding create <repo> [options]

          Creates a pipeline already bound to a git repo. The reconcile loop then materializes
          the steps from the repo's config directory (e.g. .pipelines/config.yaml). <repo> is
          the GitHub "owner/name" slug.

          --branch <name>              Branch to track (default: main)
          --path <dir>                 Config directory in the repo (default: .pipelines)
          --credentials-secret <key>   Key in the pipeline's k8s secret holding the GitHub token
                                       (omit for a public repo)
          --trigger <mode>             Deploy trigger: webhook (default), webhook-only, or poll

          Example:
            pl binding create OliverVea/Olve.Pipelines --credentials-secret GITHUB_TOKEN
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var repo = cli.Operand(0)!;
        var branch = cli.Option("branch");
        var path = cli.Option("path");
        var credentialsSecret = cli.Option("credentials-secret");

        BindingDeployTrigger? trigger = null;
        if (cli.Option("trigger") is { } triggerText)
        {
            if (BindingTrigger.Parse(triggerText).TryPickProblems(out var triggerProblems, out var parsed))
                return triggerProblems;
            trigger = parsed;
        }

        if ((await ctx.Api.CreatePipelineWithRepo(repo, branch, path, credentialsSecret, trigger, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding get &lt;pipelineId&gt;</c> — show a pipeline's GitOps binding.</summary>
public sealed class BindingGetCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a pipeline's GitOps binding";
    public string? HelpDetail => "pl binding get <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetPipelineBinding(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding status &lt;pipelineId&gt;</c> — reconcile result + declared-secret state.</summary>
public sealed class BindingStatusCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "status";
    public int RequiredOperands => 1;
    public string HelpLine => "Show binding reconcile status and declared-secret state";
    public string? HelpDetail => "pl binding status <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetPipelineBindingStatus(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var status))
            return problems;

        ctx.Output.Emit(status, CliJsonContext.Default.PipelineBindingStatus,
            () => BindingPrinter.PrintStatus(ctx.Output, status));
        return Result.Success();
    }
}

/// <summary>
/// <c>pl binding set-credentials &lt;pipelineId&gt; [secretName]</c> — set (or, with no name, clear) the
/// key in the pipeline's k8s secret holding the GitHub token used to fetch config.
/// </summary>
public sealed class BindingSetCredentialsCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "set-credentials";
    public int RequiredOperands => 1;
    public string HelpLine => "Set or clear the binding's credentials secret key";
    public string? HelpDetail =>
        """
        pl binding set-credentials <pipelineId> [secretName]

          Names the key in the pipeline's k8s secret that holds the GitHub token used to fetch
          config (moves the fetch onto the authenticated GitHub bucket). Omit <secretName> to
          clear it (revert to unauthenticated fetch).
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var pipelineId = cli.Operand(0)!;
        var secretName = cli.Operand(1); // absent => clear

        if ((await ctx.Api.UpdatePipelineBindingCredentials(pipelineId, secretName, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding set-trigger &lt;pipelineId&gt; &lt;mode&gt;</c> — change how the bound pipeline deploys.</summary>
public sealed class BindingSetTriggerCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "set-trigger";
    public int RequiredOperands => 2;
    public string HelpLine => "Set the binding's deploy trigger (webhook/webhook-only/poll)";
    public string? HelpDetail =>
        """
        pl binding set-trigger <pipelineId> <mode>

          mode is one of:
            webhook       GitHub push hook drives deploys; poll runs as a slow safety net (default)
            webhook-only  webhook only; polling is suppressed once the hook is live
            poll          no webhook; the deploy poll is the sole trigger
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var pipelineId = cli.Operand(0)!;
        if (BindingTrigger.Parse(cli.Operand(1)!).TryPickProblems(out var parseProblems, out var trigger))
            return parseProblems;

        if ((await ctx.Api.UpdatePipelineBindingDeployTrigger(pipelineId, trigger, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary>
/// <c>pl binding reconcile &lt;pipelineId&gt;</c> — apply the bound config now, off the poll schedule.
/// </summary>
public sealed class BindingReconcileCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "reconcile";
    public int RequiredOperands => 1;
    public string HelpLine => "Reconcile the bound config immediately (off the poll schedule)";
    public string? HelpDetail => "pl binding reconcile <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var pipelineId = cli.Operand(0)!;
        if ((await ctx.Api.ReconcilePipelineBinding(pipelineId, ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Reconciled binding for pipeline {pipelineId}.");
        return Result.Success();
    }
}

/// <summary>Maps the deploy-trigger CLI token to the enum, and back for display.</summary>
internal static class BindingTrigger
{
    public static Result<BindingDeployTrigger> Parse(string value) => value.ToLowerInvariant() switch
    {
        "webhook" => BindingDeployTrigger.Webhook,
        "webhook-only" or "webhookonly" => BindingDeployTrigger.WebhookOnly,
        "poll" => BindingDeployTrigger.Poll,
        _ => new ResultProblem("Unknown deploy trigger '{0}'. Expected one of: webhook, webhook-only, poll.", value),
    };

    public static string Text(BindingDeployTrigger trigger) => trigger switch
    {
        BindingDeployTrigger.Webhook => "webhook",
        BindingDeployTrigger.WebhookOnly => "webhook-only",
        BindingDeployTrigger.Poll => "poll",
        _ => trigger.ToString(),
    };
}

/// <summary>Human-readable rendering for bindings and their reconcile status.</summary>
internal static class BindingPrinter
{
    public static void PrintBinding(IOutputWriter output, PipelineConfigBinding b)
    {
        output.Line($"Binding:     {b.Id}");
        output.Line($"Pipeline:    {b.PipelineId}");
        output.Line($"Repo:        {b.Repo}@{b.Branch} ({b.Path})");
        output.Line($"Trigger:     {BindingTrigger.Text(b.DeployTrigger)}");
        output.Line($"Credentials: {b.CredentialsSecret ?? "(none)"}");
        output.Line($"Deployed:    {b.LastDeployedSha ?? "(none)"}");
        output.Line($"Synced:      {b.LastSyncedSha ?? "(none)"}");
        output.Line($"Reconcile:   {ReconcileText(b.Status.Result, b.Status.LastSyncTime)}");
        foreach (var problem in b.Status.Problems)
            output.Line($"  problem: {problem}");
    }

    public static void PrintStatus(IOutputWriter output, PipelineBindingStatus s)
    {
        output.Line($"Pipeline:  {s.PipelineId}");
        output.Line($"Repo:      {s.Repo}@{s.Branch} ({s.Path})");
        output.Line($"Reconcile: {ReconcileText(s.Result, s.LastSyncTime)}");
        output.Line($"Deployed:  {s.LastDeployedSha ?? "(none)"}");
        output.Line($"Synced:    {s.LastSyncedSha ?? "(none)"}");

        if (s.Problems.Length > 0)
        {
            output.Line("Problems:");
            foreach (var problem in s.Problems)
                output.Line($"  - {problem}");
        }

        output.Line("Secrets:");
        if (s.Secrets.Length == 0)
        {
            output.Line("  (none declared)");
            return;
        }

        var rows = s.Secrets
            .Select(sec => (IReadOnlyList<string>)
                [sec.Name, sec.IsSet switch { true => "set", false => "unset", null => "unknown" }, sec.Description ?? ""])
            .ToList();
        foreach (var line in Table.Render(["NAME", "SET", "DESCRIPTION"], rows))
            output.Line($"  {line}");
    }

    private static string ReconcileText(ReconcileResult result, DateTimeOffset? at) =>
        at is { } t ? $"{result} (last sync {t:u})" : result.ToString();
}
