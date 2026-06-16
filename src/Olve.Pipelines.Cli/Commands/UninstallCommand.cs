namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// <c>pl uninstall</c> — removes an installation (controller, MinIO, Secret, namespace),
/// retaining the MinIO data PVC unless <c>--purge-data</c>. Implemented in P1.
/// </summary>
public sealed class UninstallCommand(IProcessRunner processRunner)
{
    public async Task<Result> RunAsync(CliArgs cli, CancellationToken ct = default)
    {
        var ns = cli.Option("namespace");
        if (string.IsNullOrWhiteSpace(ns))
            return new ResultProblem("'--namespace' (-n) is required.");

        _ = processRunner;
        await Task.CompletedTask;
        return new ResultProblem("pl uninstall is not implemented yet (P1).");
    }
}
