using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Bindings;

/// <summary><c>pl binding create &lt;repo&gt;</c> — create a pipeline bound to a git repo.</summary>
public sealed class BindingCreateCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("branch", "credentials-secret", "path");
    public string Noun => "binding";
    public string Verb => "create";
    public int RequiredOperands => 1;
    public string HelpLine => "Create a pipeline bound to a repo (GitOps)";
    public string? HelpDetail =>
        """
        pl binding create <repo> [options]

          Creates a pipeline already bound to a git repo. The reconcile loop then builds the
          pipeline in the repo's binding path (.pipelines/pipeline.ts by default) into a version
          and activates it. <repo> is the GitHub "owner/name" slug.

          --branch <name>              Branch to track (default: main)
          --path <dir>                 Pipeline directory in the repo (default: .pipelines)
          --credentials-secret <key>   Key in the pipeline's k8s secret holding the GitHub token
                                       (omit for a public repo)

          Example:
            pl binding create OliverVea/Olve.Pipelines --credentials-secret GITHUB_TOKEN
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var repo = cli.Operand(0)!;
        var branch = cli.Option("branch");
        var path = cli.Option("path");
        var credentialsSecret = cli.Option("credentials-secret");

        if ((await ctx.Api.CreatePipelineWithRepo(repo, branch, path, credentialsSecret, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding get &lt;pipeline&gt;</c> — show a pipeline's GitOps binding.</summary>
public sealed class BindingGetCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a pipeline's GitOps binding";
    public string? HelpDetail => "pl binding get <pipeline>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        var pipelineId = pipeline.Id.ToString();
        if ((await ctx.Api.GetPipelineBinding(pipelineId, ct)).TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding status &lt;pipeline&gt;</c>.</summary>
public sealed class BindingStatusCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "status";
    public int RequiredOperands => 1;
    public string HelpLine => "Show the binding's reconcile status and the resulting version";
    public string? HelpDetail => "pl binding status <pipeline>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        var pipelineId = pipeline.Id.ToString();
        if ((await ctx.Api.GetPipelineBindingStatus(pipelineId, ct)).TryPickProblems(out var problems, out var status))
            return problems;

        ctx.Output.Emit(status, CliJsonContext.Default.PipelineBindingStatus,
            () => BindingPrinter.PrintStatus(ctx.Output, status));
        return Result.Success();
    }
}

/// <summary><c>pl binding set-credentials &lt;pipeline&gt; [secretName]</c>.</summary>
public sealed class BindingSetCredentialsCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "set-credentials";
    public int RequiredOperands => 1;
    public string HelpLine => "Set or clear the binding's credentials secret key";
    public string? HelpDetail =>
        """
        pl binding set-credentials <pipeline> [secretName]

          Names the key in the pipeline's secrets that holds the GitHub token used to read the
          repo (authenticated rate limit). Omit <secretName> to clear it.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        var pipelineId = pipeline.Id.ToString();
        var secretName = cli.Operand(1); // absent => clear

        if ((await ctx.Api.UpdatePipelineBindingCredentials(pipelineId, secretName, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding set-branch &lt;pipeline&gt; &lt;branch&gt;</c>.</summary>
public sealed class BindingSetBranchCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "set-branch";
    public int RequiredOperands => 2;
    public string HelpLine => "Move the binding to another branch of the same repo";
    public string? HelpDetail =>
        """
        pl binding set-branch <pipeline> <branch>

          Keeps the pipeline's runs, versions and secrets. The next poll reconciles if the branch's
          newest config commit differs, and fires the push trigger if its head differs from the
          last push.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;

        if ((await ctx.Api.SetPipelineBindingBranch(pipeline.Id.ToString(), cli.Operand(1)!, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding set-polling &lt;pipeline&gt; on|off</c>.</summary>
public sealed class BindingSetPollingCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "set-polling";
    public int RequiredOperands => 2;
    public string HelpLine => "Turn the binding's polling of GitHub on or off";
    public string? HelpDetail =>
        """
        pl binding set-polling <pipeline> on|off

          Off, the binding stops polling GitHub for config commits and branch heads: it syncs only
          on `pl binding reconcile` or its webhook. For a pipeline driven from outside, such as a
          test fixture another pipeline reconciles and triggers.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var polls = cli.Operand(1) switch { "on" => true, "off" => false, _ => (bool?)null };
        if (polls is null)
            return new ResultProblem("Polling must be on or off, got '{0}'.", cli.Operand(1)!);
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;

        if ((await ctx.Api.SetPipelineBindingPolling(pipeline.Id.ToString(), polls.Value, ct))
            .TryPickProblems(out var problems, out var binding))
            return problems;

        ctx.Output.Emit(binding, CliJsonContext.Default.PipelineConfigBinding,
            () => BindingPrinter.PrintBinding(ctx.Output, binding));
        return Result.Success();
    }
}

/// <summary><c>pl binding reconcile &lt;pipeline&gt;</c>.</summary>
public sealed class BindingReconcileCommand : ICliCommand
{
    public string Noun => "binding";
    public string Verb => "reconcile";
    public int RequiredOperands => 1;
    public string HelpLine => "Reconcile the bound config now (also retries a failed commit; --rebuild redoes an applied commit)";
    public string? HelpDetail => """
        pl binding reconcile <pipeline> [--rebuild]

          --rebuild   Reconcile the current commit again although it is applied: after a server
                      change in how manifests are read. The version changes only if the manifest does.
        """;
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "rebuild" };

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        var pipelineId = pipeline.Id.ToString();
        if ((await ctx.Api.ReconcilePipelineBinding(pipelineId, cli.HasFlag("rebuild"), ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Reconcile of {pipeline.Label} queued; follow it with `pl binding status {pipeline.Label}`.");
        return Result.Success();
    }
}

/// <summary>Human-readable rendering for bindings and their reconcile status.</summary>
internal static class BindingPrinter
{
    public static void PrintBinding(IOutputWriter output, PipelineConfigBinding b)
    {
        output.Line($"Binding:     {b.Id}");
        output.Line($"Pipeline:    {b.PipelineId}");
        output.Line($"Repo:        {b.Repo}@{b.Branch} ({b.Path})");
        output.Line($"Credentials: {b.CredentialsSecret ?? "(none)"}");
        if (!b.Polls) output.Line("Polling:     off (syncs only on reconcile or its webhook)");
        output.Line($"Last push:   {b.LastPushSha ?? "(none)"}");
        output.Line($"Reconciled:  {b.LastReconciledSha ?? "(none)"}");
        output.Line($"Reconcile:   {ReconcileText(b.Status.Result, b.Status.LastSyncTime)}");
        output.Line($"  commit:    {b.Status.Sha ?? "(none)"}{(b.Status.InProgress ? " (in progress)" : "")}");
        output.Line($"  version:   {(b.Status.VersionNumber is { } v ? $"v{v}" : "(none)")}");
        foreach (var problem in b.Status.Problems)
            output.Line($"  problem: {problem}");
        foreach (var warning in b.Status.Warnings ?? [])
            output.Line($"  warning: {warning}");
    }

    public static void PrintStatus(IOutputWriter output, PipelineBindingStatus s)
    {
        output.Line($"Pipeline:    {s.PipelineId}");
        output.Line($"Repo:        {s.Repo}@{s.Branch} ({s.Path})");
        output.Line($"Reconcile:   {ReconcileText(s.Result, s.LastSyncTime)}");
        output.Line($"  commit:    {s.ReconcileSha ?? "(none)"}{(s.ReconcileInProgress ? " (in progress)" : "")}");
        output.Line($"  version:   {(s.ReconciledVersion is { } v ? $"v{v}" : "(none)")}");
        output.Line($"Active:      {s.Activation?.ToString() ?? "(no version)"}");
        if (s.Pending is { } pending)
            output.Line($"Pending:     {pending.Describe()}");
        output.Line($"Last push:   {s.LastPushSha ?? "(none)"}");
        output.Line($"Reconciled:  {s.LastReconciledSha ?? "(none)"}");

        if (s.Problems.Length > 0)
        {
            output.Line("Problems:");
            foreach (var problem in s.Problems)
                output.Line($"  - {problem}");
        }

        if (s.Warnings.Length > 0)
        {
            output.Line("Warnings:");
            foreach (var warning in s.Warnings)
                output.Line($"  - {warning}");
        }
    }

    private static string ReconcileText(ReconcileResult result, DateTimeOffset? at) =>
        at is { } t ? $"{result} (last sync {t:u})" : result.ToString();
}
