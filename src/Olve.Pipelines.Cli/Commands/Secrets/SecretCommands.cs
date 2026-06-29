using Olve.Pipelines.Cli.Api;

namespace Olve.Pipelines.Cli.Commands.Secrets;

/// <summary><c>pl secret list &lt;pipelineId&gt;</c> — list a pipeline's secret names (values never leave the cluster).</summary>
public sealed class SecretListCommand : ICliCommand
{
    public string Noun => "secret";
    public string Verb => "list";
    public int RequiredOperands => 1;
    public string HelpLine => "List a pipeline's secret names (values are never returned)";
    public string? HelpDetail => "pl secret list <pipelineId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.ListSecrets(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var names))
            return problems;

        ctx.Output.Emit(names, CliJsonContext.Default.StringArray, () =>
        {
            if (names.Length == 0)
                ctx.Output.Line("(no secrets)");
            else
                foreach (var name in names)
                    ctx.Output.Line(name);
        });
        return Result.Success();
    }
}

/// <summary>
/// <c>pl secret set &lt;pipelineId&gt; &lt;name&gt;</c> — set a secret value. The value is read from stdin
/// by default (kept out of shell history and the process list); <c>--from-env</c> / <c>--from-file</c>
/// select other sources. On an interactive terminal with no piped input, prompts without echo.
/// </summary>
public sealed class SecretSetCommand : ICliCommand
{
    public string Noun => "secret";
    public string Verb => "set";
    public int RequiredOperands => 2;
    public string HelpLine => "Set a secret value (from stdin by default)";
    public string? HelpDetail =>
        """
        pl secret set <pipelineId> <name> [options]

          Reads the value from stdin by default, so it stays out of shell history and
          the process list. The value is sent verbatim (no trailing-newline trimming),
          so multi-line secrets such as PEM keys round-trip intact.

          --from-env <VAR>    Read the value from environment variable VAR
          --from-file <path>  Read the value from a file (verbatim)

          Examples:
            echo -n "$TOKEN" | pl secret set <pipelineId> REGISTRY_TOKEN
            pl secret set <pipelineId> REGISTRY_TOKEN --from-env TOKEN
            pl secret set <pipelineId> DEPLOY_KEY --from-file ./id_ed25519
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var pipelineId = cli.Operand(0)!;
        var name = cli.Operand(1)!;

        if (ReadValue(cli, name).TryPickProblems(out var valueProblems, out var value))
            return valueProblems;

        if ((await ctx.Api.SetSecret(pipelineId, name, value, ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Set secret '{name}' for pipeline {pipelineId}.");
        return Result.Success();
    }

    /// <summary>Resolve the secret value from (in precedence) --from-env, --from-file, then stdin/TTY.</summary>
    private static Result<string> ReadValue(CliArgs cli, string name)
    {
        var fromEnv = cli.Option("from-env");
        var fromFile = cli.Option("from-file");

        if (fromEnv is not null && fromFile is not null)
            return new ResultProblem("Specify only one of --from-env or --from-file.");

        if (fromEnv is not null)
        {
            var value = Environment.GetEnvironmentVariable(fromEnv);
            return value is null
                ? new ResultProblem("Environment variable '{0}' is not set.", fromEnv)
                : Result.Success(value);
        }

        if (fromFile is not null)
        {
            if (!File.Exists(fromFile))
                return new ResultProblem("File '{0}' not found.", fromFile);
            return Result.Success(File.ReadAllText(fromFile));
        }

        // No explicit source: read piped stdin, or prompt (no echo) on an interactive terminal.
        if (Console.IsInputRedirected)
        {
            var piped = Console.In.ReadToEnd();
            return string.IsNullOrEmpty(piped)
                ? new ResultProblem("No secret value received on stdin (use --from-env/--from-file, or pipe a value).")
                : Result.Success(piped);
        }

        var typed = ReadHidden($"Value for '{name}': ");
        return string.IsNullOrEmpty(typed)
            ? new ResultProblem("No secret value entered.")
            : Result.Success(typed);
    }

    /// <summary>Read a line from the terminal without echoing it (for interactive secret entry).</summary>
    private static string ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                    chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return new string(chars.ToArray());
    }
}

/// <summary><c>pl secret delete &lt;pipelineId&gt; &lt;name&gt;</c> — remove a secret from a pipeline.</summary>
public sealed class SecretDeleteCommand : ICliCommand
{
    public string Noun => "secret";
    public string Verb => "delete";
    public int RequiredOperands => 2;
    public string HelpLine => "Delete a secret by name";
    public string? HelpDetail => "pl secret delete <pipelineId> <name>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var pipelineId = cli.Operand(0)!;
        var name = cli.Operand(1)!;

        if ((await ctx.Api.DeleteSecret(pipelineId, name, ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Deleted secret '{name}' from pipeline {pipelineId}.");
        return Result.Success();
    }
}
