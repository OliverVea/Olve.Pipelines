using System.Security.Cryptography;
using System.Text;

namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// <c>pl bootstrap</c> — idempotent cold install of the controller + its private Garage S3 store
/// (Tier-A minimal profile). Mirrors docs/operations/environment-setup.md, automated:
/// ensure namespace → generate-if-absent storage creds → helm upgrade (minimal profile) →
/// wait for readiness (Garage creates its key + bucket at startup). Re-running converges. Named <c>bootstrap</c> (not
/// <c>install</c>) to stay distinct from the <c>install.sh</c> CLI-fetch script.
/// </summary>
public sealed class BootstrapCommand(IProcessRunner processRunner) : ICliCommand
{
    public const string ProdNamespace = "apps";
    public const string DefaultRelease = "olve-pipelines";
    public const string DefaultBucket = "olve-pipelines";
    public const string DefaultRef = "main";
    public const string DefaultImageTag = "latest";
    // Name predates the Garage migration (MinIO used it too); kept so existing installs converge.
    public const string StorageCredentialsSecret = "olve-pipelines-minio";
    public const string StorageAccessKey = "olve-pipelines";

    public string Noun => "bootstrap";
    public string Verb => "";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "allow-prod" };
    public IReadOnlyDictionary<string, string> Aliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = "namespace" };
    public string HelpLine => "Cold-install the controller + private Garage store (idempotent)";
    public string? HelpDetail =>
        """
        pl bootstrap -n <namespace> [options]   Cold-install the controller + private Garage store

          -n, --namespace <ns>     Target namespace (required)
          --release <name>         Helm release name (default: olve-pipelines)
          --bucket <name>          Storage bucket name (default: olve-pipelines)
          --image-tag <tag>        Controller image tag (default: latest)
          --image-repository <r>   Controller image repository (default: chart value)
          --image-pull-policy <p>  Controller image pull policy, e.g. Never (default: chart value)
          --ref <git-ref>          Chart git ref to pull from GitHub (default: main)
          --chart <path>           Use a local chart directory instead of pulling from GitHub
          --allow-prod             Permit installing into the prod namespace 'apps'
        """;

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct) => RunAsync(cli, ct);

    public async Task<Result> RunAsync(CliArgs cli, CancellationToken ct = default)
    {
        var ns = cli.Option("namespace");
        if (string.IsNullOrWhiteSpace(ns))
            return new ResultProblem("'--namespace' (-n) is required.");

        if (ns == ProdNamespace && !cli.HasFlag("allow-prod"))
            return new ResultProblem("Refusing to bootstrap into the prod namespace '{0}' without --allow-prod.", ns);

        var release = cli.Option("release", DefaultRelease);
        var bucket = cli.Option("bucket", DefaultBucket);
        var imageTag = cli.Option("image-tag", DefaultImageTag);
        var imageRepository = cli.Option("image-repository");
        var imagePullPolicy = cli.Option("image-pull-policy");
        var gitRef = cli.Option("ref", DefaultRef);
        var localChart = cli.Option("chart");

        if ((await ClusterPreflight.RunAsync(processRunner, ct)).TryPickProblems(out var pfProblems))
            return pfProblems;

        if ((await EnsureNamespace(ns, ct)).TryPickProblems(out var nsProblems))
            return nsProblems;

        if ((await RefuseLegacyMinioInstall(ns, release, ct)).TryPickProblems(out var legacyProblems))
            return legacyProblems;

        if ((await EnsureStorageSecret(ns, ct)).TryPickProblems(out var secProblems))
            return secProblems;

        var chartResult = await new ChartFetcher().ResolveAsync(localChart, ChartFetcher.DefaultRepo, gitRef, ct);
        if (chartResult.TryPickProblems(out var chartProblems, out var chart))
            return chartProblems;

        try
        {
            var image = new ImageOverrides(imageTag, imageRepository, imagePullPolicy);
            if ((await HelmUpgrade(ns, release, bucket, image, chart.ChartDirectory, ct)).TryPickProblems(out var helmProblems))
                return helmProblems;

            // Garage creates the access key + bucket itself at startup (--default-bucket).
            Step($"Waiting for Garage ({release}-garage) to be ready");
            if ((await RolloutStatus(ns, $"{release}-garage", "120s", ct)).TryPickProblems(out var garageProblems))
                return garageProblems;

            Step($"Waiting for the controller ({release}) to become ready");
            if ((await RolloutStatus(ns, release, "180s", ct)).TryPickProblems(out var ctrlProblems))
                return ctrlProblems;
        }
        finally
        {
            ChartFetcher.TryDelete(chart.TempRoot);
        }

        Step($"Done. Controller '{release}' is ready in namespace '{ns}'.");
        return Result.Success();
    }

    private async Task<Result> EnsureNamespace(string ns, CancellationToken ct)
    {
        var get = await processRunner.RunAsync("kubectl", ["get", "namespace", ns], ct: ct);
        if (get.TryPickProblems(out var problems, out var output))
            return problems;

        if (output.Succeeded)
            return Result.Success();

        Step($"Creating namespace '{ns}'");
        return Forget(await processRunner.RunCheckedAsync("kubectl", ["create", "namespace", ns], ct: ct));
    }

    // A pre-Garage install keeps its data in the MinIO PVC; upgrading it in place would point the
    // controller at an empty Garage bucket. Make that an explicit migration, not a silent reset.
    private async Task<Result> RefuseLegacyMinioInstall(string ns, string release, CancellationToken ct)
    {
        var minio = await processRunner.RunAsync("kubectl", ["get", "deployment", $"{release}-minio", "-n", ns], ct: ct);
        if (minio.TryPickProblems(out var minioProblems, out var minioOutput))
            return minioProblems;
        if (!minioOutput.Succeeded)
            return Result.Success();

        var garage = await processRunner.RunAsync("kubectl", ["get", "deployment", $"{release}-garage", "-n", ns], ct: ct);
        if (garage.TryPickProblems(out var garageProblems, out var garageOutput))
            return garageProblems;
        if (garageOutput.Succeeded)
            return Result.Success();

        return new ResultProblem(
            "Release '{0}' in '{1}' still stores its data in MinIO. Re-running bootstrap would switch it to an empty "
            + "Garage store; migrate the data first (docs/operations/environment-setup.md, \"Migrating from MinIO\").",
            release, ns);
    }

    private async Task<Result> EnsureStorageSecret(string ns, CancellationToken ct)
    {
        var get = await processRunner.RunAsync("kubectl",
            ["get", "secret", StorageCredentialsSecret, "-n", ns], ct: ct);
        if (get.TryPickProblems(out var problems, out var output))
            return problems;

        if (output.Succeeded)
            return await EnsureRpcSecret(ns, ct);

        // Generate-if-absent: the cluster Secret is the source of truth. Never regenerate on
        // re-run (would rotate the key out from under a running Garage).
        Step($"Generating storage credentials secret '{StorageCredentialsSecret}'");
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        return Forget(await processRunner.RunCheckedAsync("kubectl",
        [
            "create", "secret", "generic", StorageCredentialsSecret, "-n", ns,
            $"--from-literal=root-user={StorageAccessKey}",
            $"--from-literal=root-password={password}",
            $"--from-literal=rpc-secret={NewRpcSecret()}",
        ], ct: ct));
    }

    // Secrets created before the Garage migration lack Garage's cluster RPC secret: add just that
    // key, leaving the access key/secret (which Garage imports as-is) untouched.
    private async Task<Result> EnsureRpcSecret(string ns, CancellationToken ct)
    {
        var existing = await GetSecretValue(ns, "rpc-secret", ct);
        if (existing.TryPickProblems(out var problems, out var value))
            return problems;

        if (value.Length > 0)
        {
            Step($"Storage credentials secret '{StorageCredentialsSecret}' already exists — leaving untouched");
            return Result.Success();
        }

        Step($"Adding Garage rpc-secret to existing secret '{StorageCredentialsSecret}'");
        return Forget(await processRunner.RunCheckedAsync("kubectl",
        [
            "patch", "secret", StorageCredentialsSecret, "-n", ns, "--type", "merge",
            "-p", "{\"stringData\":{\"rpc-secret\":\"" + NewRpcSecret() + "\"}}",
        ], ct: ct));
    }

    private static string NewRpcSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private readonly record struct ImageOverrides(string Tag, string? Repository, string? PullPolicy);

    private async Task<Result> HelmUpgrade(
        string ns, string release, string bucket, ImageOverrides image, string chartDir, CancellationToken ct)
    {
        Step($"Deploying release '{release}' (minimal profile) into '{ns}'");
        var endpoint = $"http://{release}-garage.{ns}:3900";
        var args = new List<string>
        {
            "upgrade", "--install", release, chartDir,
            "-n", ns,
            "-f", Path.Combine(chartDir, "values-minimal.yaml"),
            "--set", $"config.Storage__Endpoint={endpoint}",
            "--set", $"config.Storage__Bucket={bucket}",
            "--set", $"config.Kubernetes__Namespace={ns}",
            "--set", $"garage.bucket={bucket}",
            "--set", $"image.tag={image.Tag}",
        };
        if (!string.IsNullOrWhiteSpace(image.Repository))
            args.AddRange(["--set", $"image.repository={image.Repository}"]);
        if (!string.IsNullOrWhiteSpace(image.PullPolicy))
            args.AddRange(["--set", $"image.pullPolicy={image.PullPolicy}"]);

        return Forget(await processRunner.RunCheckedAsync("helm", args, ct: ct));
    }

    private async Task<Result<string>> GetSecretValue(string ns, string key, CancellationToken ct)
    {
        var result = await processRunner.RunCheckedAsync("kubectl",
            ["get", "secret", StorageCredentialsSecret, "-n", ns, "-o", $"jsonpath={{.data.{key}}}"], ct: ct);
        if (result.TryPickProblems(out var problems, out var output))
            return problems;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(output.StandardOutput.Trim()));
        }
        catch (FormatException)
        {
            return new ResultProblem("Secret '{0}' key '{1}' is not valid base64.", StorageCredentialsSecret, key);
        }
    }

    private async Task<Result> RolloutStatus(string ns, string deployment, string timeout, CancellationToken ct)
        => Forget(await processRunner.RunCheckedAsync("kubectl",
            ["-n", ns, "rollout", "status", $"deploy/{deployment}", $"--timeout={timeout}"], ct: ct));

    /// <summary>Collapses a <see cref="Result{T}"/> we only care about for success/failure into a <see cref="Result"/>.</summary>
    private static Result Forget(Result<ProcessResult> result)
        => result.TryPickProblems(out var problems) ? problems : Result.Success();

    private static void Step(string message) => Console.WriteLine($"==> {message}");
}
