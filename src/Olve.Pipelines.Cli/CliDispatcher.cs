using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Bindings;
using Olve.Pipelines.Cli.Commands.Bundles;
using Olve.Pipelines.Cli.Commands.Jobs;
using Olve.Pipelines.Cli.Commands.Pipelines;
using Olve.Pipelines.Cli.Commands.Processing;
using Olve.Pipelines.Cli.Commands.Production;
using Olve.Pipelines.Cli.Commands.Secrets;
using Olve.Pipelines.Cli.Commands.Triggers;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli;

/// <summary>
/// Parses argv, routes to a noun-verb command, renders problems, and returns an exit code.
/// The command registry is built explicitly (one <c>Register</c> per command) — no reflection.
/// </summary>
public sealed class CliDispatcher
{
    // Global flags accepted by every command. --help is handled by the router itself.
    private static readonly HashSet<string> GlobalFlags =
        new(StringComparer.Ordinal) { "json", "verbose", "help" };

    private static readonly Dictionary<string, string> GlobalAliases =
        new(StringComparer.Ordinal) { ["h"] = "help", ["v"] = "verbose" };

    private readonly List<ICliCommand> _commands = [];
    private readonly Dictionary<string, ICliCommand> _byKey = new(StringComparer.Ordinal);

    public CliDispatcher(IProcessRunner processRunner)
    {
        Register(new BootstrapCommand(processRunner));
        Register(new TeardownCommand(processRunner));
        Register(new LoginCommand());

        // pipeline
        Register(new PipelineListCommand());
        Register(new PipelineGetCommand());
        Register(new PipelineDocumentCommand());
        Register(new PipelineDeleteCommand());

        // production
        Register(new ProductionListCommand());
        Register(new ProductionGetCommand());
        Register(new ProductionConfigCommand());
        Register(new ProductionTriggerCommand());

        // processing
        Register(new ProcessingListCommand());
        Register(new ProcessingGetCommand());
        Register(new ProcessingConfigCommand());
        Register(new ProcessingPromotionsCommand());
        Register(new ProcessingPromotionCommand());
        Register(new ProcessingBlockCommand());
        Register(new ProcessingUnblockCommand());
        Register(new ProcessingRePromoteCommand());

        // job
        Register(new JobListCommand());
        Register(new JobGetCommand());
        Register(new JobLogsCommand());
        Register(new JobQueueCommand());
        Register(new JobCancelCommand());

        // bundle
        Register(new BundleListCommand());
        Register(new BundleGetCommand());

        // trigger
        Register(new TriggerListCommand());
        Register(new TriggerGetCommand());

        // secret
        Register(new SecretListCommand());
        Register(new SecretSetCommand());
        Register(new SecretDeleteCommand());

        // binding
        Register(new BindingCreateCommand());
        Register(new BindingGetCommand());
        Register(new BindingStatusCommand());
        Register(new BindingSetCredentialsCommand());
        Register(new BindingSetTriggerCommand());
        Register(new BindingReconcileCommand());
    }

    private void Register(ICliCommand command)
    {
        _commands.Add(command);
        _byKey.Add(CliCommand.Key(command.Noun, command.Verb), command);
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (args.Count == 0)
        {
            PrintUsage();
            return 2; // no command supplied — usage error
        }

        var helpRequested = args.Any(a => a is "--help" or "-h");
        var peek0 = args[0].StartsWith('-') ? null : args[0];
        var peek1 = args.Count > 1 && !args[1].StartsWith('-') ? args[1] : null;

        // `pl --help` / leading flag with no command.
        if (peek0 is null)
        {
            PrintUsage();
            return helpRequested ? 0 : 2;
        }

        // `pl help` / `pl help <noun>`.
        if (peek0 is "help")
        {
            if (peek1 is not null && HasNoun(peek1))
                PrintNounHelp(peek1);
            else
                PrintUsage();
            return 0;
        }

        // Resolve the command: prefer a noun-verb match, then a verbless one.
        ICliCommand? command = null;
        var offset = 0;
        if (peek1 is not null && _byKey.TryGetValue(CliCommand.Key(peek0, peek1), out command))
            offset = 2;
        else if (_byKey.TryGetValue(peek0, out command))
            offset = 1;

        if (command is null)
        {
            if (HasNoun(peek0))
            {
                if (helpRequested || peek1 is null)
                {
                    PrintNounHelp(peek0);
                    return helpRequested ? 0 : 2; // noun given without a verb is a usage error
                }

                return Fail([new ResultProblem("Unknown command '{0} {1}'. Run `pl {0} --help`.", peek0, peek1)]);
            }

            return Fail([new ResultProblem("Unknown command '{0}'. Run `pl help`.", peek0)]);
        }

        if (helpRequested)
        {
            PrintCommandHelp(command);
            return 0;
        }

        var booleanFlags = new HashSet<string>(GlobalFlags, StringComparer.Ordinal);
        booleanFlags.UnionWith(command.BooleanFlags);
        var aliases = new Dictionary<string, string>(GlobalAliases, StringComparer.Ordinal);
        foreach (var (k, v) in command.Aliases)
            aliases[k] = v;

        if (CliArgs.Parse(args, booleanFlags, aliases).TryPickProblems(out var parseProblems, out var cli))
            return Fail(parseProblems);

        cli.OperandOffset = offset;

        if (cli.OperandCount < command.RequiredOperands)
        {
            return Fail([new ResultProblem(
                "`pl {0}` expects {1} argument(s). Run `pl {0} --help`.",
                CliCommand.Key(command.Noun, command.Verb), command.RequiredOperands)]);
        }

        var verbose = cli.HasFlag("verbose");
        var log = new StderrLog(verbose);

        if (CliConfigStore.Load().TryPickProblems(out var configProblems, out var config))
            return Fail(configProblems);

        // Pre-flight refresh of the cached token so commands go out with a fresh bearer (best-effort).
        await TokenRefresh.EnsureFreshAsync(cli, config, log, ct);

        if (ApiClientFactory.Create(cli, config, log).TryPickProblems(out var clientProblems, out var api))
            return Fail(clientProblems);

        var ctx = new CommandContext
        {
            Json = cli.HasFlag("json"),
            Verbose = verbose,
            Api = api,
            Output = new ConsoleOutputWriter(cli.HasFlag("json")),
            Log = log,
            Config = config,
        };

        var result = await command.Execute(cli, ctx, ct);
        return result.TryPickProblems(out var problems) ? Fail(problems) : 0;
    }

    private bool HasNoun(string noun) => _commands.Any(c => c.Noun == noun);

    private static int Fail(IEnumerable<ResultProblem> problems)
    {
        foreach (var problem in problems)
            Console.Error.WriteLine($"error: {problem}");
        return 1;
    }

    private void PrintUsage()
    {
        Console.WriteLine("pl — Olve.Pipelines operator + API CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  pl <command> [args] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var command in _commands)
            Console.WriteLine($"  {Signature(command),-28} {command.HelpLine}");
        Console.WriteLine();
        Console.WriteLine("Global options (any command):");
        Console.WriteLine("  --json        Machine-readable JSON output on stdout");
        Console.WriteLine("  --verbose,-v  Diagnostic logging to stderr");
        Console.WriteLine("  --help,-h     Show help for the command");
        Console.WriteLine();
        Console.WriteLine("Run `pl <command> --help` for command-specific options.");
    }

    private void PrintNounHelp(string noun)
    {
        Console.WriteLine($"pl {noun} — commands:");
        Console.WriteLine();
        foreach (var command in _commands.Where(c => c.Noun == noun))
            Console.WriteLine($"  {Signature(command),-28} {command.HelpLine}");
    }

    private static void PrintCommandHelp(ICliCommand command)
    {
        if (command.HelpDetail is { } detail)
            Console.WriteLine(detail);
        else
            Console.WriteLine($"{Signature(command)}   {command.HelpLine}");
    }

    private static string Signature(ICliCommand command) => CliCommand.Key(command.Noun, command.Verb);
}
