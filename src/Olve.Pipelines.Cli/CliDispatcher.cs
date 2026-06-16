using Olve.Pipelines.Cli.Commands;

namespace Olve.Pipelines.Cli;

/// <summary>Parses argv, routes to a command, renders problems, and returns an exit code.</summary>
public sealed class CliDispatcher(IProcessRunner processRunner)
{
    private static readonly HashSet<string> BooleanFlags =
        new(StringComparer.Ordinal) { "allow-prod", "purge-data", "help" };

    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.Ordinal) { ["n"] = "namespace", ["h"] = "help" };

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (CliArgs.Parse(args, BooleanFlags, Aliases).TryPickProblems(out var parseProblems, out var cli))
            return Fail(parseProblems);

        if (cli.Command is null)
        {
            PrintUsage();
            return 2; // no command supplied — usage error
        }

        if (cli.Command is "help" or "--help" || cli.HasFlag("help"))
        {
            PrintUsage();
            return 0;
        }

        var result = cli.Command switch
        {
            "install" => await new InstallCommand(processRunner).RunAsync(cli, ct),
            "uninstall" => await new UninstallCommand(processRunner).RunAsync(cli, ct),
            _ => (Result)new ResultProblem("Unknown command '{0}'. Run `pl help`.", cli.Command),
        };

        return result.TryPickProblems(out var problems) ? Fail(problems) : 0;
    }

    private static int Fail(IEnumerable<ResultProblem> problems)
    {
        foreach (var problem in problems)
            Console.Error.WriteLine($"error: {problem}");
        return 1;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        pl — Olve.Pipelines operator CLI

        Usage:
          pl install   -n <namespace> [options]   Install the controller + private MinIO
          pl uninstall -n <namespace> [options]   Remove an installation
          pl help                                  Show this help

        install options:
          -n, --namespace <ns>   Target namespace (required)
          --release <name>       Helm release name (default: olve-pipelines)
          --bucket <name>        MinIO bucket name (default: olve-pipelines)
          --image-tag <tag>      Controller image tag (default: latest)
          --image-repository <r> Controller image repository (default: chart value)
          --image-pull-policy <p> Controller image pull policy, e.g. Never (default: chart value)
          --ref <git-ref>        Chart git ref to pull from GitHub (default: main)
          --chart <path>         Use a local chart directory instead of pulling from GitHub
          --allow-prod           Permit installing into the prod namespace 'apps'

        uninstall options:
          -n, --namespace <ns>   Target namespace (required)
          --release <name>       Helm release name (default: olve-pipelines)
          --purge-data           Also delete the MinIO data PVC (full wipe)
        """);
}
