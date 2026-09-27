namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// <c>pl teardown</c> — reverse of <c>pl bootstrap</c>, idempotent. Removes the helm release
/// (controller, Garage, RBAC, ingress) and the storage creds Secret. The Garage data PVCs carry
/// <c>helm.sh/resource-policy: keep</c>, so they survive a normal teardown and are only deleted
/// with <c>--purge-data</c> (which also removes a pre-Garage MinIO PVC, if one is left). Re-running on a partial/already-gone install converges.
/// </summary>
public sealed class TeardownCommand(IProcessRunner processRunner) : ICliCommand
{
    public string Noun => "teardown";
    public string Verb => "";
    public IReadOnlySet<string> BooleanFlags { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "allow-prod", "purge-data" };
    public IReadOnlyDictionary<string, string> Aliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = "namespace" };
    public string HelpLine => "Remove an installation (release + storage creds; data PVCs retained)";
    public string? HelpDetail =>
        """
        pl teardown -n <namespace> [options]   Remove an installation

          -n, --namespace <ns>   Target namespace (required)
          --release <name>       Helm release name (default: olve-pipelines)
          --purge-data           Also delete the storage data PVCs (full wipe)
        """;

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct) => RunAsync(cli, ct);

    public async Task<Result> RunAsync(CliArgs cli, CancellationToken ct = default)
    {
        var ns = cli.Option("namespace");
        if (string.IsNullOrWhiteSpace(ns))
            return new ResultProblem("'--namespace' (-n) is required.");

        if (ns == BootstrapCommand.ProdNamespace && !cli.HasFlag("allow-prod"))
            return new ResultProblem("Refusing to tear down the prod namespace '{0}' without --allow-prod.", ns);

        var release = cli.Option("release", BootstrapCommand.DefaultRelease);
        var purgeData = cli.HasFlag("purge-data");

        if ((await ClusterPreflight.RunAsync(processRunner, ct)).TryPickProblems(out var pfProblems))
            return pfProblems;

        if ((await HelmUninstall(ns, release, ct)).TryPickProblems(out var helmProblems))
            return helmProblems;

        if ((await DeleteStorageSecret(ns, ct)).TryPickProblems(out var secProblems))
            return secProblems;

        if (purgeData && (await DeleteDataPvcs(ns, release, ct)).TryPickProblems(out var pvcProblems))
            return pvcProblems;

        Step($"Done. Release '{release}' removed from '{ns}'"
            + (purgeData ? " (storage data purged)." : " (storage data PVCs retained)."));
        return Result.Success();
    }

    private async Task<Result> HelmUninstall(string ns, string release, CancellationToken ct)
    {
        // Idempotency: a missing release exits non-zero with "release: not found". Probe with
        // `helm status` first and treat a non-existent release as already-gone.
        var status = await processRunner.RunAsync("helm", ["status", release, "-n", ns], ct: ct);
        if (status.TryPickProblems(out var statusProblems, out var statusOutput))
            return statusProblems;

        if (!statusOutput.Succeeded)
        {
            Step($"Release '{release}' not found in '{ns}' — already removed");
            return Result.Success();
        }

        Step($"Uninstalling release '{release}' from '{ns}'");
        return Forget(await processRunner.RunCheckedAsync("helm", ["uninstall", release, "-n", ns], ct: ct));
    }

    private async Task<Result> DeleteStorageSecret(string ns, CancellationToken ct)
    {
        // The creds Secret is created by `pl bootstrap` via kubectl (not helm), so helm uninstall
        // leaves it behind. --ignore-not-found keeps this idempotent.
        Step($"Deleting storage credentials secret '{BootstrapCommand.StorageCredentialsSecret}'");
        return Forget(await processRunner.RunCheckedAsync("kubectl",
            ["delete", "secret", BootstrapCommand.StorageCredentialsSecret, "-n", ns, "--ignore-not-found"], ct: ct));
    }

    private async Task<Result> DeleteDataPvcs(string ns, string release, CancellationToken ct)
    {
        // --purge-data only: the PVCs are retained across a normal uninstall, so this explicit
        // delete is the sole path that wipes stored data. The MinIO PVC is from pre-Garage installs.
        string[] pvcs = [$"{release}-garage-meta", $"{release}-garage-data", $"{release}-minio-data"];
        Step($"Purging storage data volumes {string.Join(", ", pvcs)}");
        return Forget(await processRunner.RunCheckedAsync("kubectl",
            ["delete", "pvc", ..pvcs, "-n", ns, "--ignore-not-found"], ct: ct));
    }

    /// <summary>Collapses a <see cref="Result{T}"/> we only care about for success/failure into a <see cref="Result"/>.</summary>
    private static Result Forget(Result<ProcessResult> result)
        => result.TryPickProblems(out var problems) ? problems : Result.Success();

    private static void Step(string message) => Console.WriteLine($"==> {message}");
}
