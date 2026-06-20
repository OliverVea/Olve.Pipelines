using System.Security.Cryptography;
using System.Text;

namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// <c>pl install</c> — idempotent cold install of the controller + its private MinIO
/// (Tier-A minimal profile). Mirrors docs/operations/environment-setup.md, automated:
/// ensure namespace → generate-if-absent MinIO creds → helm upgrade (minimal profile) →
/// create bucket → wait for readiness. Re-running converges.
/// </summary>
public sealed class InstallCommand(IProcessRunner processRunner) : ICliCommand
{
    public const string ProdNamespace = "apps";
    public const string DefaultRelease = "olve-pipelines";
    public const string DefaultBucket = "olve-pipelines";
    public const string DefaultRef = "main";
    public const string DefaultImageTag = "latest";
    public const string MinioCredentialsSecret = "olve-pipelines-minio";
    public const string MinioRootUser = "olve-pipelines";

    public string Noun => "install";
    public string Verb => "";
    public IReadOnlySet<string> BooleanFlags { get; } = new HashSet<string>(StringComparer.Ordinal) { "allow-prod" };
    public IReadOnlyDictionary<string, string> Aliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = "namespace" };
    public string HelpLine => "Install the controller + private MinIO (cold install, idempotent)";
    public string? HelpDetail =>
        """
        pl install -n <namespace> [options]   Install the controller + private MinIO

          -n, --namespace <ns>     Target namespace (required)
          --release <name>         Helm release name (default: olve-pipelines)
          --bucket <name>          MinIO bucket name (default: olve-pipelines)
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
            return new ResultProblem("Refusing to install into the prod namespace '{0}' without --allow-prod.", ns);

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

        if ((await EnsureMinioSecret(ns, ct)).TryPickProblems(out var secProblems))
            return secProblems;

        var chartResult = await new ChartFetcher().ResolveAsync(localChart, ChartFetcher.DefaultRepo, gitRef, ct);
        if (chartResult.TryPickProblems(out var chartProblems, out var chart))
            return chartProblems;

        try
        {
            var image = new ImageOverrides(imageTag, imageRepository, imagePullPolicy);
            if ((await HelmUpgrade(ns, release, bucket, image, chart.ChartDirectory, ct)).TryPickProblems(out var helmProblems))
                return helmProblems;

            Step($"Waiting for MinIO ({release}-minio) to be ready");
            if ((await RolloutStatus(ns, $"{release}-minio", "120s", ct)).TryPickProblems(out var minioProblems))
                return minioProblems;

            if ((await EnsureBucket(ns, release, bucket, ct)).TryPickProblems(out var bucketProblems))
                return bucketProblems;

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

    private async Task<Result> EnsureMinioSecret(string ns, CancellationToken ct)
    {
        var get = await processRunner.RunAsync("kubectl",
            ["get", "secret", MinioCredentialsSecret, "-n", ns], ct: ct);
        if (get.TryPickProblems(out var problems, out var output))
            return problems;

        if (output.Succeeded)
        {
            Step($"MinIO credentials secret '{MinioCredentialsSecret}' already exists — leaving untouched");
            return Result.Success();
        }

        // Generate-if-absent: the cluster Secret is the source of truth. Never regenerate on
        // re-run (would rotate the password out from under a running MinIO).
        Step($"Generating MinIO credentials secret '{MinioCredentialsSecret}'");
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        return Forget(await processRunner.RunCheckedAsync("kubectl",
        [
            "create", "secret", "generic", MinioCredentialsSecret, "-n", ns,
            $"--from-literal=root-user={MinioRootUser}",
            $"--from-literal=root-password={password}",
        ], ct: ct));
    }

    private readonly record struct ImageOverrides(string Tag, string? Repository, string? PullPolicy);

    private async Task<Result> HelmUpgrade(
        string ns, string release, string bucket, ImageOverrides image, string chartDir, CancellationToken ct)
    {
        Step($"Deploying release '{release}' (minimal profile) into '{ns}'");
        var endpoint = $"http://{release}-minio.{ns}:9000";
        var args = new List<string>
        {
            "upgrade", "--install", release, chartDir,
            "-n", ns,
            "-f", Path.Combine(chartDir, "values-minimal.yaml"),
            "--set", $"config.Storage__Endpoint={endpoint}",
            "--set", $"config.Storage__Bucket={bucket}",
            "--set", $"config.Kubernetes__Namespace={ns}",
            "--set", $"minio.bucket={bucket}",
            "--set", $"image.tag={image.Tag}",
        };
        if (!string.IsNullOrWhiteSpace(image.Repository))
            args.AddRange(["--set", $"image.repository={image.Repository}"]);
        if (!string.IsNullOrWhiteSpace(image.PullPolicy))
            args.AddRange(["--set", $"image.pullPolicy={image.PullPolicy}"]);

        return Forget(await processRunner.RunCheckedAsync("helm", args, ct: ct));
    }

    private async Task<Result> EnsureBucket(string ns, string release, string bucket, CancellationToken ct)
    {
        Step($"Ensuring bucket '{bucket}' exists");

        var credsResult = await ReadMinioCredentials(ns, ct);
        if (credsResult.TryPickProblems(out var credProblems, out var creds))
            return credProblems;

        var (user, password) = creds;
        var mcHost = $"http://{user}:{password}@{release}-minio.{ns}:9000";
        var podName = $"pl-minio-mb-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

        // MinIO is ClusterIP-only, so create the bucket from inside the cluster via a one-shot
        // mc pod. --ignore-existing makes it idempotent.
        return Forget(await processRunner.RunCheckedAsync("kubectl",
        [
            "run", podName, "-n", ns, "--rm", "-i", "--restart=Never",
            "--image=minio/mc:latest",
            $"--env=MC_HOST_local={mcHost}",
            "--command", "--", "mc", "mb", "--ignore-existing", $"local/{bucket}",
        ], ct: ct));
    }

    private async Task<Result<(string User, string Password)>> ReadMinioCredentials(string ns, CancellationToken ct)
    {
        var user = await GetSecretValue(ns, "root-user", ct);
        if (user.TryPickProblems(out var up, out var userValue))
            return up;
        var password = await GetSecretValue(ns, "root-password", ct);
        if (password.TryPickProblems(out var pp, out var passwordValue))
            return pp;
        return (userValue, passwordValue);
    }

    private async Task<Result<string>> GetSecretValue(string ns, string key, CancellationToken ct)
    {
        var result = await processRunner.RunCheckedAsync("kubectl",
            ["get", "secret", MinioCredentialsSecret, "-n", ns, "-o", $"jsonpath={{.data.{key}}}"], ct: ct);
        if (result.TryPickProblems(out var problems, out var output))
            return problems;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(output.StandardOutput.Trim()));
        }
        catch (FormatException)
        {
            return new ResultProblem("Secret '{0}' key '{1}' is not valid base64.", MinioCredentialsSecret, key);
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
