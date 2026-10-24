using System.Globalization;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Config;

/// <summary><c>pl config get &lt;pipeline&gt; [--snapshot N]</c> — the config document as YAML, exactly as saved.</summary>
public sealed class ConfigGetCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("snapshot");
    public string Noun => "config";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Print the pipeline's config document (YAML) or an older snapshot";
    public string? HelpDetail =>
        """
        pl config get <pipeline> [--snapshot N] > app.yml

          Prints the current config snapshot (or snapshot N) as YAML, exactly as it was saved.
          Defaults are not written into it; see `pl config check` for the effective config.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (ConfigCommandSupport.IntOption(cli, "snapshot").TryPickProblems(out var optionProblems, out var snapshot))
            return optionProblems;
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        if ((await ctx.Api.GetPipelineConfig(pipeline.Id.ToString(), snapshot, ct)).TryPickProblems(out var problems, out var doc))
            return problems;

        ctx.Output.Emit(doc, CliJsonContext.Default.ConfigDocument, () =>
        {
            if (doc.Snapshot is null)
                Console.Error.WriteLine($"{pipeline.Label} has no config yet (pl config set {pipeline.Label} -f <file>).");
            else if (doc.Yaml.Length > 0)
                ctx.Output.Line(doc.Yaml.TrimEnd('\n', '\r'));
        });
        return Result.Success();
    }
}

/// <summary><c>pl config set &lt;pipeline&gt; -f app.yml</c> — validate and save a new snapshot.</summary>
public sealed class ConfigSetCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("file");
    public IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["f"] = "file" };
    public string Noun => "config";
    public string Verb => "set";
    public int RequiredOperands => 1;
    public string HelpLine => "Validate a YAML config document and save it as a new snapshot";
    public string? HelpDetail =>
        """
        pl config set <pipeline> -f app.yml     (or pipe the YAML on stdin)

          Saves the whole document as the next snapshot (config #N). It must be valid for the
          active version, so a typo can't break what is running; keys the active version
          doesn't declare are kept (config for a future version can be saved first). The
          result for a pending version is shown too, and that version activates as soon as
          its config and secrets are satisfied.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var file = cli.Option("file");
        string yaml;
        if (file is not null)
        {
            if (!File.Exists(file))
                return new ResultProblem("File '{0}' not found.", file);
            yaml = await File.ReadAllTextAsync(file, ct);
        }
        else if (Console.IsInputRedirected)
            yaml = await Console.In.ReadToEndAsync(ct);
        else
            return new ResultProblem("Give the document with -f <file>, or pipe it on stdin.");

        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        if ((await ctx.Api.SetPipelineConfig(pipeline.Id.ToString(), yaml, ct)).TryPickProblems(out var problems, out var saved))
            return problems;

        ctx.Output.Emit(saved, CliJsonContext.Default.ConfigSaveResponse, () => ConfigCommandSupport.PrintSaved(ctx.Output, pipeline.Label, saved));
        return Result.Success();
    }
}

/// <summary><c>pl config history &lt;pipeline&gt;</c>.</summary>
public sealed class ConfigHistoryCommand : ICliCommand
{
    public string Noun => "config";
    public string Verb => "history";
    public int RequiredOperands => 1;
    public string HelpLine => "List the config snapshots: number, author, time";
    public string? HelpDetail => "pl config history <pipeline>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        if ((await ctx.Api.ListPipelineConfigSnapshots(pipeline.Id.ToString(), ct)).TryPickProblems(out var problems, out var snapshots))
            return problems;

        ctx.Output.Emit(snapshots, CliJsonContext.Default.ConfigSnapshotInfoArray, () =>
        {
            if (snapshots.Length == 0)
            {
                ctx.Output.Line("(no config saved)");
                return;
            }
            var rows = snapshots.Select(s => (IReadOnlyList<string>)
                [$"#{s.Number}", s.Author, Time(s.CreatedAt), s.RestoredFrom is { } r ? $"rollback to #{r}" : ""]).ToList();
            foreach (var line in Table.Render(["CONFIG", "AUTHOR", "SAVED", "NOTE"], rows))
                ctx.Output.Line(line);
        });
        return Result.Success();
    }
}

/// <summary><c>pl config rollback &lt;pipeline&gt; &lt;N&gt;</c> — save snapshot N again as the newest.</summary>
public sealed class ConfigRollbackCommand : ICliCommand
{
    public string Noun => "config";
    public string Verb => "rollback";
    public int RequiredOperands => 2;
    public string HelpLine => "Save an old config snapshot again as the newest one";
    public string? HelpDetail =>
        """
        pl config rollback <pipeline> <N>

          Saves snapshot N's content as a new snapshot (#latest+1), validated like `pl config set`.
          Runs that already started keep the snapshot they pinned.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (!int.TryParse(cli.Operand(1)!.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return new ResultProblem("'{0}' is not a snapshot number.", cli.Operand(1)!);
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        if ((await ctx.Api.RollbackPipelineConfig(pipeline.Id.ToString(), number, ct)).TryPickProblems(out var problems, out var saved))
            return problems;

        ctx.Output.Emit(saved, CliJsonContext.Default.ConfigSaveResponse, () => ConfigCommandSupport.PrintSaved(ctx.Output, pipeline.Label, saved));
        return Result.Success();
    }
}

/// <summary><c>pl config check &lt;pipeline&gt; [--version V]</c>.</summary>
public sealed class ConfigCheckCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("version");
    public string Noun => "config";
    public string Verb => "check";
    public int RequiredOperands => 1;
    public string HelpLine => "Validate the current config against a version (default: pending, else active)";
    public string? HelpDetail =>
        """
        pl config check <pipeline> [--version V]

          Checks the current config document against version V (default: the pending version,
          else the active one): problems, secrets it references that are not set, defaults V
          changes that the document relies on, and the effective config (defaults applied).
          Exits 1 when the version could not activate with this config.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (ConfigCommandSupport.IntOption(cli, "version").TryPickProblems(out var optionProblems, out var version))
            return optionProblems;
        if ((await ResolvePipelineAsync(ctx, cli.Operand(0)!, ct)).TryPickProblems(out var resolveProblems, out var pipeline))
            return resolveProblems;
        if ((await ctx.Api.CheckPipelineConfig(pipeline.Id.ToString(), version, ct)).TryPickProblems(out var problems, out var check))
            return problems;

        ctx.Output.Emit(check, CliJsonContext.Default.ConfigCheckResponse, () => ConfigCommandSupport.PrintCheck(ctx.Output, check));
        return check.Satisfied
            ? Result.Success()
            : new ResultProblem("Version {0} can't activate with this config.", check.Version);
    }
}

internal static class ConfigCommandSupport
{
    public static Result<int?> IntOption(CliArgs cli, string name)
    {
        var text = cli.Option(name);
        if (text is null)
            return Result.Success<int?>(null);
        return int.TryParse(text.TrimStart('v', '#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : new ResultProblem("--{0} must be a number, got '{1}'.", name, text);
    }

    public static void PrintSaved(IOutputWriter o, string label, ConfigSaveResponse saved)
    {
        o.Line($"Saved config #{saved.Snapshot.Number} of {label}{(saved.Snapshot.RestoredFrom is { } r ? $" (content of #{r})" : "")}.");
        if (saved.ActivatedVersion is { } activated)
            o.Line($"Activated v{activated}: its config and secrets are satisfied.");
        if (saved.Active is { } active && !active.Satisfied)
            o.Line($"active v{active.Version}: {string.Join("; ", Reasons(active))}");
        if (saved.Pending is { } pending)
        {
            o.Line($"pending v{pending.Version}: {(pending.Satisfied ? "satisfied" : string.Join("; ", Reasons(pending)))}");
            foreach (var d in pending.ChangedDefaults)
                o.Line($"  v{pending.Version} changes the default of {d.Path} ({d.From} → {d.To})");
        }
    }

    public static void PrintCheck(IOutputWriter o, ConfigCheckResponse c)
    {
        o.Line($"v{c.Version}: {(c.Satisfied ? "satisfied" : c.Valid ? "valid, but secrets are missing" : "invalid")}");
        foreach (var problem in c.Problems)
            o.Line($"  config {problem}");
        foreach (var secret in c.MissingSecrets)
            o.Line($"  secret {secret} not set");
        foreach (var d in c.ChangedDefaults)
            o.Line($"  v{c.Version} changes the default of {d.Path} ({d.From} → {d.To})");
        if (c.ReferencedSecrets.Length > 0)
            o.Line($"secrets:  {string.Join(", ", c.ReferencedSecrets)}");
        if (c.Effective is { } effective)
            o.Line($"effective: {effective}");
    }

    private static IEnumerable<string> Reasons(ConfigCheckResponse c)
        => c.Problems.Select(p => $"config {p}").Concat(c.MissingSecrets.Select(s => $"secret {s} not set"));
}
