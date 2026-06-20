namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// <c>pl uninstall</c> — reverse of install, idempotent. Removes the helm release (controller,
/// MinIO, RBAC, ingress) and the MinIO creds Secret. The MinIO data PVC carries
/// <c>helm.sh/resource-policy: keep</c>, so it survives a normal uninstall and is only deleted
/// with <c>--purge-data</c>. Re-running on a partial/already-gone install converges.
/// </summary>
public sealed class UninstallCommand(IProcessRunner processRunner) : ICliCommand
{
    public string Noun => "uninstall";
    public string Verb => "";
    public IReadOnlySet<string> BooleanFlags { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "allow-prod", "purge-data" };
    public IReadOnlyDictionary<string, string> Aliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = "namespace" };
    public string HelpLine => "Remove an installation (release + MinIO creds; data PVC retained)";
    public string? HelpDetail =>
        """
        pl uninstall -n <namespace> [options]   Remove an installation

          -n, --namespace <ns>   Target namespace (required)
          --release <name>       Helm release name (default: olve-pipelines)
          --purge-data           Also delete the MinIO data PVC (full wipe)
        """;

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct) => RunAsync(cli, ct);

    public async Task<Result> RunAsync(CliArgs cli, CancellationToken ct = default)
    {
        var ns = cli.Option("namespace");
        if (string.IsNullOrWhiteSpace(ns))
            return new ResultProblem("'--namespace' (-n) is required.");

        if (ns == InstallCommand.ProdNamespace && !cli.HasFlag("allow-prod"))
            return new ResultProblem("Refusing to uninstall from the prod namespace '{0}' without --allow-prod.", ns);

        var release = cli.Option("release", InstallCommand.DefaultRelease);
        var purgeData = cli.HasFlag("purge-data");

        if ((await ClusterPreflight.RunAsync(processRunner, ct)).TryPickProblems(out var pfProblems))
            return pfProblems;

        if ((await HelmUninstall(ns, release, ct)).TryPickProblems(out var helmProblems))
            return helmProblems;

        if ((await DeleteMinioSecret(ns, ct)).TryPickProblems(out var secProblems))
            return secProblems;

        if (purgeData && (await DeleteMinioPvc(ns, release, ct)).TryPickProblems(out var pvcProblems))
            return pvcProblems;

        Step($"Done. Release '{release}' removed from '{ns}'"
            + (purgeData ? " (MinIO data purged)." : " (MinIO data PVC retained)."));
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

    private async Task<Result> DeleteMinioSecret(string ns, CancellationToken ct)
    {
        // The creds Secret is created by `pl install` via kubectl (not helm), so helm uninstall
        // leaves it behind. --ignore-not-found keeps this idempotent.
        Step($"Deleting MinIO credentials secret '{InstallCommand.MinioCredentialsSecret}'");
        return Forget(await processRunner.RunCheckedAsync("kubectl",
            ["delete", "secret", InstallCommand.MinioCredentialsSecret, "-n", ns, "--ignore-not-found"], ct: ct));
    }

    private async Task<Result> DeleteMinioPvc(string ns, string release, CancellationToken ct)
    {
        // --purge-data only: the PVC is retained across a normal uninstall, so this explicit
        // delete is the sole path that wipes MinIO data.
        var pvc = $"{release}-minio-data";
        Step($"Purging MinIO data volume '{pvc}'");
        return Forget(await processRunner.RunCheckedAsync("kubectl",
            ["delete", "pvc", pvc, "-n", ns, "--ignore-not-found"], ct: ct));
    }

    /// <summary>Collapses a <see cref="Result{T}"/> we only care about for success/failure into a <see cref="Result"/>.</summary>
    private static Result Forget(Result<ProcessResult> result)
        => result.TryPickProblems(out var problems) ? problems : Result.Success();

    private static void Step(string message) => Console.WriteLine($"==> {message}");
}
